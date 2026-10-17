using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Authoring;

public sealed record DraftSnapshot(ApplicationVersionGraph Definition, byte[] Revision);
