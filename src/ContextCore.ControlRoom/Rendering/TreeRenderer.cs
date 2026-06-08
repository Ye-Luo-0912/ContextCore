using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

/// <summary>将关系图谱渲染为控制台树形文本的静态工具类。</summary>
public static class TreeRenderer
{
    public static void RenderRelationGraph(RelationGraph graph)
    {
        Console.WriteLine();
        Console.WriteLine($"Relations for {graph.Id}");
        Console.WriteLine(new string('=', 14 + graph.Id.Length));

        Console.WriteLine(graph.Id);
        Console.WriteLine("|");
        Console.WriteLine("+-- incoming");
        if (graph.Upstream.Count == 0)
        {
            Console.WriteLine("|   +-- (none)");
        }
        else
        {
            foreach (var relation in graph.Upstream)
            {
                Console.WriteLine($"|   +-- {relation.SourceId} --{relation.RelationType}({relation.Weight:0.00}, {relation.Confidence:0.00})--> {graph.Id}");
            }
        }

        Console.WriteLine("+-- outgoing");
        if (graph.Downstream.Count == 0)
        {
            Console.WriteLine("    +-- (none)");
        }
        else
        {
            foreach (var relation in graph.Downstream)
            {
                Console.WriteLine($"    +-- {graph.Id} --{relation.RelationType}({relation.Weight:0.00}, {relation.Confidence:0.00})--> {relation.TargetId}");
            }
        }
    }
}
