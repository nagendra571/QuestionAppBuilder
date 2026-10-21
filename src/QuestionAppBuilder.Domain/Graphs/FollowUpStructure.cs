using QuestionAppBuilder.Domain.Entities;

namespace QuestionAppBuilder.Domain.Graphs;

public static class FollowUpStructure
{
    public static Element? Parent(Element element, IEnumerable<Element> elements)
        => element.ParentOptionId is null && element.ParentOption is null ? null : elements.FirstOrDefault(candidate => candidate.Question?.Options.Any(option =>
            ReferenceEquals(option, element.ParentOption) || element.ParentOptionId is int id && option.Id == id) == true);

    public static IReadOnlyList<Element> Branch(Element root, IEnumerable<Element> elements)
    {
        var all = elements.OrderBy(e => e.SortOrder).ThenBy(e => e.Id).ToArray();
        var children = Parents(all).ToLookup(pair => pair.Value, pair => pair.Key);
        var result = new List<Element>();
        Visit(root, children, new HashSet<Element>(), result);
        return result;
    }

    public static int Depth(Element element, IEnumerable<Element> elements)
    {
        if (element.ParentOptionId is null && element.ParentOption is null) return 0;
        var parents = Parents(elements.ToArray());
        var visited = new HashSet<Element> { element };
        var depth = 0;
        while (parents.TryGetValue(element, out var parent) && visited.Add(parent)) { depth++; element = parent; }
        return depth;
    }

    public static IReadOnlyList<Element> Ordered(IEnumerable<Element> elements)
    {
        var all = elements.OrderBy(e => e.SortOrder).ThenBy(e => e.Id).ToArray();
        var children = Parents(all).ToLookup(pair => pair.Value, pair => pair.Key);
        var result = new List<Element>();
        var seen = new HashSet<Element>();
        foreach (var root in all.Where(e => e.ParentOptionId is null && e.ParentOption is null)) Visit(root, children, seen, result);
        // Keep malformed/orphaned elements visible to definition validation.
        foreach (var item in all) if (seen.Add(item)) result.Add(item);
        return result;
    }

    private static Dictionary<Element, Element> Parents(IReadOnlyList<Element> elements)
    {
        var byId = new Dictionary<int, Element>();
        var byReference = new Dictionary<QuestionOption, Element>();
        foreach (var parent in elements)
        foreach (var option in parent.Question?.Options ?? Enumerable.Empty<QuestionOption>())
        {
            if (option.Id != 0) byId.TryAdd(option.Id, parent);
            byReference[option] = parent;
        }
        var parents = new Dictionary<Element, Element>();
        foreach (var element in elements)
        {
            if (element.ParentOption is { } option && byReference.TryGetValue(option, out var parent)) parents[element] = parent;
            else if (element.ParentOptionId is int id && byId.TryGetValue(id, out parent)) parents[element] = parent;
        }
        return parents;
    }

    private static void Visit(Element root, ILookup<Element, Element> children, HashSet<Element> seen, List<Element> result)
    {
        var pending = new Stack<Element>();
        pending.Push(root);
        while (pending.TryPop(out var element))
        {
            if (!seen.Add(element)) continue;
            result.Add(element);
            foreach (var child in children[element].Reverse()) pending.Push(child);
        }
    }
}
