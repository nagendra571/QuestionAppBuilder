using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Infrastructure.Responses;

public sealed class ResponseDocumentService(IDbContextFactory<QuestionAppBuilderDbContext> factory,
    IResponseService responses, IDefinitionEvaluator evaluator) : IResponseDocumentService
{
    public async Task<ResponseSnapshot> UploadAsync(Guid responseId, int questionId, byte[] expectedRevision,
        string fileName, Stream content, CancellationToken ct = default)
    {
        var snapshot = await EditableAsync(responseId, questionId, expectedRevision, ct);
        fileName = Path.GetFileName(fileName.Replace('\\', '/')).Trim();
        if (fileName.Length is 0 or > 255 || fileName.Any(char.IsControl))
            throw new ArgumentException("Choose a document with a filename of 255 characters or fewer.");
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > IResponseDocumentService.MaximumBytes)
                throw new ArgumentException("Choose a document smaller than or equal to 10 MB.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        var bytes = buffer.ToArray();
        ValidateFile(fileName, bytes);
        await MutateAsync(snapshot, questionId, new ResponseDocument
        { ResponseDraftId = responseId, QuestionId = questionId, FileName = fileName, Content = bytes }, ct);
        return await responses.GetAsync(responseId, ct);
    }

    public async Task<ResponseSnapshot> RemoveAsync(Guid responseId, int questionId, byte[] expectedRevision, CancellationToken ct = default)
    {
        var snapshot = await EditableAsync(responseId, questionId, expectedRevision, ct);
        await MutateAsync(snapshot, questionId, null, ct);
        return await responses.GetAsync(responseId, ct);
    }

    public async Task<ResponseDocumentDownload?> DownloadAsync(Guid responseId, int questionId, CancellationToken ct = default)
    {
        await responses.GetAsync(responseId, ct); // Enforces owner and email-grant scope.
        await using var db = await factory.CreateDbContextAsync(ct);
        return await ReadAsync(db, responseId, questionId, ct);
    }

    public async Task<ResponseDocumentDownload?> DownloadSubmissionAsync(int submissionId, int questionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var responseId = await db.Submissions.AsNoTracking()
            .Where(s => s.Id == submissionId && s.Answers.Any(a => a.QuestionId == questionId))
            .Select(s => s.ResponseId).SingleOrDefaultAsync(ct);
        return responseId is Guid id ? await ReadAsync(db, id, questionId, ct) : null;
    }

    private static Task<ResponseDocumentDownload?> ReadAsync(QuestionAppBuilderDbContext db, Guid responseId, int questionId, CancellationToken ct)
        => db.ResponseDocuments.AsNoTracking().Where(d => d.ResponseDraftId == responseId && d.QuestionId == questionId)
            .Select(d => new ResponseDocumentDownload(d.FileName, d.Content)).SingleOrDefaultAsync(ct);

    private async Task<ResponseSnapshot> EditableAsync(Guid responseId, int questionId, byte[] expectedRevision, CancellationToken ct)
    {
        var snapshot = await responses.GetAsync(responseId, ct);
        if (snapshot.Status != ResponseStatus.InProgress || !snapshot.Revision.AsSpan().SequenceEqual(expectedRevision))
            throw new ResponseConflictException();
        if (!snapshot.Definition.Categories.SelectMany(c => c.Elements).Any(e => e.Question?.Id == questionId
                && e.Question.AnswerType == AnswerType.UploadDocument)
            || !evaluator.Evaluate(snapshot.Definition, snapshot.Answers, false).VisibleQuestionIds.Contains(questionId))
            throw new NotFoundException("The document question was not found.");
        return snapshot;
    }

    private async Task MutateAsync(ResponseSnapshot snapshot, int questionId, ResponseDocument? replacement, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var response = await db.ResponseDrafts.Include(r => r.Answers).SingleAsync(r => r.Id == snapshot.Id, ct);
        if (response.Status != ResponseStatus.InProgress || !response.RevisionNonce.ToByteArray().AsSpan().SequenceEqual(snapshot.Revision))
            throw new ResponseConflictException();
        var existing = await db.ResponseDocuments.FindAsync([snapshot.Id, questionId], ct);
        var answer = response.Answers.SingleOrDefault(a => a.QuestionId == questionId);
        if (replacement is null)
        {
            if (existing is not null) db.ResponseDocuments.Remove(existing);
            if (answer is not null) response.Answers.Remove(answer);
        }
        else
        {
            if (existing is null) db.ResponseDocuments.Add(replacement);
            else { existing.FileName = replacement.FileName; existing.Content = replacement.Content; }
            if (answer is null)
            {
                answer = new ResponseAnswer { Id = Guid.NewGuid(), ResponseDraftId = snapshot.Id,
                    QuestionId = questionId, TextValue = replacement.FileName };
                response.Answers.Add(answer);
                db.ResponseAnswers.Add(answer);
            }
            else answer.TextValue = replacement.FileName;
        }
        response.RevisionNonce = Guid.NewGuid();
        response.UpdatedUtc = DateTime.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException exception) { throw new ResponseConflictException(exception); }
        catch (DbUpdateException exception) when (exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 })
        { throw new ResponseConflictException(exception); }
    }

    private static void ValidateFile(string fileName, byte[] bytes)
    {
        if (bytes.Length == 0) throw new ArgumentException("The document is empty. Choose a file with content.");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var valid = false;
        if (extension == ".pdf") valid = bytes.AsSpan().StartsWith("%PDF-"u8);
        if (extension == ".txt")
        {
            try { valid = !new UTF8Encoding(false, true).GetString(bytes).Any(c => char.IsControl(c) && c is not '\t' and not '\r' and not '\n'); }
            catch (DecoderFallbackException) { }
        }
        if (extension == ".docx")
        {
            try
            {
                using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                valid = archive.GetEntry("[Content_Types].xml") is not null && archive.GetEntry("word/document.xml") is not null
                    && !archive.Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase));
            }
            catch (InvalidDataException) { }
        }
        if (!valid) throw new ArgumentException("Choose a valid PDF, Word (.docx), or UTF-8 plain-text (.txt) document. Other formats and macro-enabled documents are not supported.");
    }
}
