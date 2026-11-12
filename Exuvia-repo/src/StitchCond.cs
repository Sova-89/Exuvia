// ============================================================================
//  StitchCond.cs — условия из conditions.json.
//  Раньше было частью Stitch.cs (общее описание — там).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Exuvia
{
    /// Состояние игры, от которого зависят условия.
    internal sealed class CondCtx
    {
        internal string Crest = "";
        internal string Scene = "";
        internal Func<string, bool> PackActive;
        internal Dictionary<string, CondNode> Named;
        internal List<string> Unknown = new List<string>(); // что не поняли (для лога)
    }

    // ---- условия -------------------------------------------------------------

    internal abstract class CondNode
    {
        internal abstract bool Eval(CondCtx ctx, int depth);
    }

    internal sealed class CondConst : CondNode
    {
        readonly bool value;
        internal CondConst(bool v) { value = v; }
        internal override bool Eval(CondCtx ctx, int depth) { return value; }
    }

    internal sealed class CondAll : CondNode
    {
        internal readonly List<CondNode> Items = new List<CondNode>();
        internal override bool Eval(CondCtx ctx, int depth)
        {
            foreach (CondNode node in Items) if (!node.Eval(ctx, depth)) return false;
            return true;
        }
    }

    internal sealed class CondAny : CondNode
    {
        internal readonly List<CondNode> Items = new List<CondNode>();
        internal override bool Eval(CondCtx ctx, int depth)
        {
            foreach (CondNode node in Items) if (node.Eval(ctx, depth)) return true;
            return false;
        }
    }

    internal sealed class CondNot : CondNode
    {
        internal CondNode Inner;
        internal override bool Eval(CondCtx ctx, int depth) { return !Inner.Eval(ctx, depth); }
    }

    internal sealed class CondLeaf : CondNode
    {
        internal CondKind Kind;
        internal readonly List<string> Values = new List<string>();
        internal override bool Eval(CondCtx ctx, int depth)
        {
            foreach (string v in Values)
            {
                switch (Kind)
                {
                    case CondKind.Crest:
                        if (string.Equals(ctx.Crest, v, StringComparison.OrdinalIgnoreCase)) return true;
                        break;
                    case CondKind.Scene:
                        if (string.Equals(ctx.Scene, v, StringComparison.OrdinalIgnoreCase)) return true;
                        break;
                    case CondKind.SceneContains:
                        if (ctx.Scene != null && ctx.Scene.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                        break;
                    case CondKind.PackActive:
                        if (ctx.PackActive != null && ctx.PackActive(v)) return true;
                        break;
                }
            }
            return false;
        }
    }

    internal sealed class CondNamed : CondNode
    {
        internal string Name;
        internal override bool Eval(CondCtx ctx, int depth)
        {
            if (depth > 16 || ctx.Named == null) return false;
            CondNode node;
            if (!ctx.Named.TryGetValue(Name, out node))
            {
                if (!ctx.Unknown.Contains("условие «" + Name + "»")) ctx.Unknown.Add("условие «" + Name + "»");
                return false;
            }
            return node.Eval(ctx, depth + 1);
        }
    }

    /// Условие, которое мы не умеем читать (область, акт, боссы…). Для Stitchwork это тоже «ложь».
    internal sealed class CondUnknown : CondNode
    {
        internal string What;
        internal override bool Eval(CondCtx ctx, int depth)
        {
            if (!ctx.Unknown.Contains(What)) ctx.Unknown.Add(What);
            return false;
        }
    }

    internal static class Cond
    {
        internal static CondNode Parse(JToken token)
        {
            if (token == null) return new CondConst(true);
            switch (token.Type)
            {
                case JTokenType.Boolean:
                    return new CondConst((bool)token);
                case JTokenType.String:
                    return new CondNamed { Name = (string)token };
                case JTokenType.Array:
                    {
                        var all = new CondAll();
                        foreach (JToken item in (JArray)token) all.Items.Add(Parse(item));
                        return all;
                    }
                case JTokenType.Object:
                    {
                        var all = new CondAll();
                        foreach (JProperty prop in ((JObject)token).Properties()) all.Items.Add(ParseProperty(prop));
                        return all.Items.Count == 1 ? all.Items[0] : all;
                    }
            }
            return new CondUnknown { What = "условие вида " + token.Type };
        }

        static CondNode ParseProperty(JProperty prop)
        {
            string key = prop.Name;
            JToken token = prop.Value;
            switch (key.ToLowerInvariant())
            {
                case "all":
                case "and":
                    {
                        var node = new CondAll();
                        AddItems(node.Items, token);
                        return node;
                    }
                case "any":
                case "or":
                    {
                        var node = new CondAny();
                        AddItems(node.Items, token);
                        return node;
                    }
                case "not":
                    return new CondNot { Inner = Parse(token) };
                case "crest":
                    return Leaf(CondKind.Crest, token, key);
                case "scene":
                    return Leaf(CondKind.Scene, token, key);
                case "scenecontains":
                    return Leaf(CondKind.SceneContains, token, key);
                case "packactive":
                    return Leaf(CondKind.PackActive, token, key);
            }
            return new CondUnknown { What = "условие «" + key + "»" };
        }

        static void AddItems(List<CondNode> list, JToken token)
        {
            if (token is JArray) { foreach (JToken item in (JArray)token) list.Add(Parse(item)); }
            else list.Add(Parse(token));
        }

        static CondNode Leaf(CondKind kind, JToken token, string key)
        {
            var leaf = new CondLeaf { Kind = kind };
            if (token.Type == JTokenType.String) leaf.Values.Add((string)token);
            else if (token is JArray)
            {
                foreach (JToken item in (JArray)token)
                {
                    if (item.Type != JTokenType.String)
                        return new CondUnknown { What = "условие «" + key + "» со сложным значением" };
                    leaf.Values.Add((string)item);
                }
            }
            else return new CondUnknown { What = "условие «" + key + "» со сложным значением" };
            return leaf;
        }
    }

    // ---- пак -----------------------------------------------------------------
}
