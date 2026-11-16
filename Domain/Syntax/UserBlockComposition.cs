namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Syntax;

/// <summary>
/// 可复用的属性组：一组<b>属性声明</b>，被多个元素定义用 <c>&lt;uses ref="…"/&gt;</c> 引用。
/// 属性组只贡献属性声明，不携带结构、围栏或渲染信息；它本身不是元素，不能出现在正文里。
/// </summary>
public sealed record UserBlockAttributeGroupDefinition
{
    /// <summary>稳定标识，与元素 Id 同一套标识符规则。</summary>
    public required string Id { get; init; }

    /// <summary>组内属性声明（按声明顺序保存，展开时按此顺序追加）。</summary>
    public IReadOnlyList<UserBlockAttributeDeclaration> Attributes { get; init; } = [];
}

/// <summary>
/// 可复用的结构片段：一棵部件子树，被元素定义用 <c>&lt;include ref="…"/&gt;</c> 引用。
/// 片段只描述结构，不声明槽位；引用者要在自己的 <c>slots</c> 里登记片段用到的自定义槽名。
/// </summary>
public sealed record UserBlockStructureFragmentDefinition
{
    /// <summary>稳定标识。</summary>
    public required string Id { get; init; }

    /// <summary>片段根部件；为 null 表示片段没有可用结构（引用它即缺引用）。</summary>
    public UserBlockPart? Root { get; init; }
}

/// <summary>
/// 定义间组合的目录：属性组 + 结构片段。与元素定义同存在 <c>.md.elements.xml</c> 里，
/// 装载期被 <see cref="UserBlockCompositionRules"/> 展开进引用者，渲染器与解析器看不到引用。
/// </summary>
public sealed record UserBlockComposition
{
    /// <summary>没有组合单元的单例：历史定义文件都取它。</summary>
    public static UserBlockComposition Empty { get; } = new();

    public IReadOnlyList<UserBlockAttributeGroupDefinition> AttributeGroups { get; init; } = [];

    public IReadOnlyList<UserBlockStructureFragmentDefinition> Fragments { get; init; } = [];

    public bool IsEmpty => AttributeGroups.Count == 0 && Fragments.Count == 0;
}

/// <summary>
/// 定义间组合与继承的校验和展开。一条规则同时服务两个调用点，只是策略不同：
/// <list type="bullet">
/// <item>保存（<c>UserBlockDefinitionValidator</c>）：任一问题都是 Error，拒绝保存；</item>
/// <item>装载（<c>UserBlockElementsSerializer</c>）：丢该条定义 + 诊断，其余定义照常装载。</item>
/// </list>
///
/// 展开是纯函数：先按 <c>extends</c> 把父链合并成普通定义，再展开属性组与结构片段。
/// 输出普通 <see cref="UserBlockDefinition"/>，结构里不再有 include 节点，
/// 属性是“自有 + 属性组”的合并结果，子部件已继承父部件。因此“组合/继承表达”与
/// “手工内联表达”产物完全一致，渲染器与解析器永远看不到 ref 与 extends。
/// </summary>
public static class UserBlockCompositionRules
{
    /// <summary>环（片段直接或间接包含自己）。</summary>
    public const string CycleCode = "USER_ELEMENTS_COMPOSITION_CYCLE";

    /// <summary>缺引用：<c>uses</c>/<c>include</c>/<c>extends</c> 指向不存在的单元。</summary>
    public const string MissingRefCode = "USER_ELEMENTS_COMPOSITION_MISSING_REF";

    /// <summary>部件名碰撞：展开后同一份结构里出现两个相同槽位的部件。</summary>
    public const string PartCollisionCode = "USER_ELEMENTS_COMPOSITION_PART_COLLISION";

    /// <summary>展开的递归深度上限：环已在校验里拒绝，这里只兜运行期栈安全。</summary>
    private const int MaxExpansionDepth = 64;

    /// <summary>该定义是否声明了组合（属性组或结构片段）。</summary>
    public static bool UsesComposition(UserBlockDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.AttributeGroups.Count > 0 || ContainsInclude(definition.Structure.Root);
    }

    /// <summary>该定义是否声明了继承（<c>extends</c>）。</summary>
    public static bool UsesInheritance(UserBlockDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return !string.IsNullOrEmpty(definition.Extends);
    }

    /// <summary>结构树里是否还有未展开的 include 节点。</summary>
    public static bool ContainsInclude(UserBlockPart? part)
    {
        if (part is null) return false;
        if (part.Include is not null) return true;

        foreach (var child in part.Children)
        {
            if (ContainsInclude(child)) return true;
        }

        return false;
    }

    /// <summary>
    /// 该定义引用到的全部单元 Id（去重、保持声明顺序，含片段内部的传递引用）。
    /// 进语法指纹：只改片段定义也要让引用者的缓存键变化。
    /// </summary>
    public static IReadOnlyList<string> CollectReferenceIds(
        UserBlockDefinition definition,
        UserBlockComposition? composition = null) =>
        CollectReferenceIds(definition, composition, scope: null);

    /// <summary>
    /// 同上，并带上 <c>extends</c> 父链（直接父在前、逐级向上）。
    /// 父定义也在引用集合里，只改父定义就能换掉子定义的缓存键。
    /// <paramref name="scope"/> 是解析父链用的定义集合；装载期传整份定义列表。
    /// </summary>
    public static IReadOnlyList<string> CollectReferenceIds(
        UserBlockDefinition definition,
        UserBlockComposition? composition,
        IEnumerable<UserBlockDefinition>? scope)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var empty = composition ?? UserBlockComposition.Empty;
        var parents = IndexDefinitions(scope);

        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var cursor = definition;
        var guard = 0;
        while (!string.IsNullOrEmpty(cursor.Extends) && guard++ < MaxExpansionDepth)
        {
            var parentId = cursor.Extends!;
            if (!seen.Add(parentId)) break;
            ordered.Add(parentId);
            if (!parents.TryGetValue(parentId, out var parent)) break;
            cursor = parent;
        }

        // 继承来的属性组/片段也算引用：先合并父链，再按合并结果收集。
        var resolved = ResolveInheritance(definition, scope, report: null) ?? definition;

        foreach (var id in resolved.AttributeGroups)
        {
            if (seen.Add(id)) ordered.Add(id);
        }

        var fragments = IndexFragments(empty);
        CollectIncludeIds(resolved.Structure.Root, fragments, seen, ordered, depth: 0);
        return ordered;
    }

    /// <summary>
    /// 校验一条定义的组合是否成立。返回空表表示可以展开。
    /// 保存期把任一问题当 Error 拒绝；装载期丢该条定义并给同样码的诊断。
    /// </summary>
    public static IReadOnlyList<UserBlockStructureIssue> Validate(
        UserBlockDefinition definition,
        UserBlockComposition? composition) =>
        Validate(definition, composition, scope: null);

    /// <summary>
    /// 同上，<paramref name="scope"/> 是解析 <c>extends</c> 父链用的定义集合。
    /// 保存期传“现有定义 + 候选定义”，装载期传整份读入的定义列表——
    /// 两处调用的都是这一个重载，只是对问题的处理策略不同。
    /// </summary>
    public static IReadOnlyList<UserBlockStructureIssue> Validate(
        UserBlockDefinition definition,
        UserBlockComposition? composition,
        IEnumerable<UserBlockDefinition>? scope)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var empty = composition ?? UserBlockComposition.Empty;
        var issues = new List<UserBlockStructureIssue>();
        var seenMessages = new HashSet<string>(StringComparer.Ordinal);

        void Report(string code, string message)
        {
            if (seenMessages.Add(code + "\u0000" + message))
            {
                issues.Add(new UserBlockStructureIssue(code, message));
            }
        }

        // 继承先于组合：父链合并成普通定义后再判缺引用/环/碰撞。
        // 环与缺引用复用组合的同一套码：作者看到的是一类问题，不是两套规则。
        var resolved = ResolveInheritance(definition, scope, Report);
        if (resolved is null)
        {
            return issues;
        }

        ValidateResolved(resolved, empty, issues, Report);
        return issues;
    }

    private static void ValidateResolved(
        UserBlockDefinition definition,
        UserBlockComposition empty,
        List<UserBlockStructureIssue> issues,
        Action<string, string> Report)
    {
        if (!UsesComposition(definition))
        {
            return;
        }

        var groups = new Dictionary<string, UserBlockAttributeGroupDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in empty.AttributeGroups)
        {
            groups.TryAdd(group.Id, group);
        }

        var fragments = new Dictionary<string, UserBlockStructureFragmentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var fragment in empty.Fragments)
        {
            fragments.TryAdd(fragment.Id, fragment);
        }

        foreach (var id in definition.AttributeGroups)
        {
            if (!groups.ContainsKey(id))
            {
                Report(MissingRefCode, $"属性组引用 '{id}' 不存在，无法展开。");
            }
        }

        foreach (var id in EnumerateIncludeIds(definition.Structure.Root))
        {
            if (!fragments.TryGetValue(id, out var fragment) || fragment.Root is null)
            {
                Report(MissingRefCode, $"结构片段引用 '{id}' 不存在或没有可用结构，无法展开。");
                continue;
            }

            DetectCycle(id, fragment, fragments, new List<string>(), Report);
        }

        // 缺引用/环时不做后续检查：展开结果不可信，报“部件碰撞”只会误导。
        if (issues.Count > 0)
        {
            return;
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in EnumerateAttributeKeys(definition, empty))
        {
            if (!keys.Add(key))
            {
                Report(
                    "USER_ELEMENTS_ATTRIBUTE_DUPLICATE",
                    $"属性键 '{key}' 在“自有声明 + 属性组”合并后重复，请删除其中一条。");
            }
        }

        var expanded = Expand(definition, empty);
        if (expanded.Structure.Root is not null)
        {
            var slots = new HashSet<string>(StringComparer.Ordinal);
            CollectCollisions(expanded.Structure.Root, slots, Report);
        }
    }

    /// <summary>
    /// 把引用展开成普通定义：属性按“自有 → 属性组（声明顺序）”合并，
    /// 结构里每个 include 节点替换为片段根部件（递归展开）。
    /// 保留 <see cref="UserBlockDefinition.AttributeGroups"/> 与
    /// <see cref="UserBlockDefinition.CompositionRefs"/> 作为来源信息：
    /// 前者供保存写回 ref 原文，后者供缓存键携带引用集合。
    /// </summary>
    public static UserBlockDefinition Expand(UserBlockDefinition definition, UserBlockComposition? composition) =>
        Expand(definition, composition, scope: null);

    /// <summary>
    /// 同上，<paramref name="scope"/> 用于解析 <c>extends</c> 父链。
    /// 继承先于组合：父链在装载边界合并成普通定义（<see cref="UserBlockDefinition.Extends"/> 清空），
    /// 随后的属性组/片段展开只面对一份已经“铺平”的定义；
    /// 因此渲染器与解析器永远看不到 <c>extends</c>。
    /// </summary>
    public static UserBlockDefinition Expand(
        UserBlockDefinition definition,
        UserBlockComposition? composition,
        IEnumerable<UserBlockDefinition>? scope)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var empty = composition ?? UserBlockComposition.Empty;
        var resolved = ResolveInheritance(definition, scope, report: null) ?? definition;
        var attributeGroups = resolved.AttributeGroups;
        var references = CollectReferenceIds(definition, empty, scope);

        if (attributeGroups.Count == 0 && !ContainsInclude(resolved.Structure.Root))
        {
            return resolved.CompositionRefs.Count == references.Count
                && resolved.CompositionRefs.SequenceEqual(references, StringComparer.Ordinal)
                    ? resolved
                    : resolved with { CompositionRefs = references };
        }

        var groups = new Dictionary<string, UserBlockAttributeGroupDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in empty.AttributeGroups)
        {
            groups.TryAdd(group.Id, group);
        }

        var attributes = new List<UserBlockAttributeDeclaration>(resolved.Attributes.Count + 8);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in resolved.Attributes)
        {
            if (keys.Add(attribute.Key)) attributes.Add(attribute);
        }

        foreach (var id in attributeGroups)
        {
            if (!groups.TryGetValue(id, out var group)) continue;
            foreach (var attribute in group.Attributes)
            {
                if (keys.Add(attribute.Key)) attributes.Add(attribute);
            }
        }

        var fragments = IndexFragments(empty);
        var root = ExpandPart(resolved.Structure.Root, fragments, depth: 0);

        return resolved with
        {
            Attributes = attributes,
            Structure = resolved.Structure with { Root = root },
            CompositionRefs = references
        };
    }

    /// <summary>整组展开；没有组合/继承的定义原样返回（引用相等）。</summary>
    public static IReadOnlyList<UserBlockDefinition> ExpandAll(
        IEnumerable<UserBlockDefinition>? definitions,
        UserBlockComposition? composition)
    {
        if (definitions is null) return [];

        var all = definitions as IReadOnlyList<UserBlockDefinition> ?? definitions.ToList();
        var result = new List<UserBlockDefinition>(all.Count);
        foreach (var definition in all)
        {
            result.Add(UsesComposition(definition) || UsesInheritance(definition) || definition.CompositionRefs.Count > 0
                ? Expand(definition, composition, all)
                : definition);
        }

        return result;
    }

    private static Dictionary<string, UserBlockStructureFragmentDefinition> IndexFragments(
        UserBlockComposition? composition)
    {
        var index = new Dictionary<string, UserBlockStructureFragmentDefinition>(StringComparer.OrdinalIgnoreCase);
        if (composition is null) return index;

        foreach (var fragment in composition.Fragments)
        {
            index.TryAdd(fragment.Id, fragment);
        }

        return index;
    }

    private static Dictionary<string, UserBlockDefinition> IndexDefinitions(
        IEnumerable<UserBlockDefinition>? definitions)
    {
        var index = new Dictionary<string, UserBlockDefinition>(StringComparer.OrdinalIgnoreCase);
        if (definitions is null) return index;

        foreach (var definition in definitions)
        {
            index.TryAdd(definition.Id, definition);
        }

        return index;
    }

    /// <summary>
    /// 把 <c>extends</c> 父链合并成普通定义（父在前、子在后逐级覆盖）。
    /// 无继承时原样返回（引用相等）；环/缺引用返回 null，并由 <paramref name="report"/> 报固定码：
    /// 环用 <see cref="CycleCode"/>、父定义缺失用 <see cref="MissingRefCode"/>，与组合共用同一套码。
    /// </summary>
    private static UserBlockDefinition? ResolveInheritance(
        UserBlockDefinition definition,
        IEnumerable<UserBlockDefinition>? scope,
        Action<string, string>? report)
    {
        if (!UsesInheritance(definition)) return definition;

        var parents = IndexDefinitions(scope);
        var chain = new List<UserBlockDefinition> { definition };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { definition.Id };
        var cursor = definition;

        while (!string.IsNullOrEmpty(cursor.Extends))
        {
            var parentId = cursor.Extends!;
            if (!visited.Add(parentId))
            {
                // 成环时补上重复的父 Id，消息里能直接看出“谁指向谁”。
                var path = string.Join(" → ", chain.Select(node => node.Id).Append(parentId));
                report?.Invoke(CycleCode, $"定义继承成环：{path}。环上的定义无法展开，必须拆开。");
                return null;
            }

            if (!parents.TryGetValue(parentId, out var parent))
            {
                report?.Invoke(MissingRefCode, $"继承的父定义 '{parentId}' 不存在，无法展开。");
                return null;
            }

            chain.Add(parent);
            cursor = parent;
            if (chain.Count > MaxExpansionDepth)
            {
                var path = string.Join(" → ", chain.Select(node => node.Id));
                report?.Invoke(CycleCode, $"定义继承成环：{path}。环上的定义无法展开，必须拆开。");
                return null;
            }
        }

        // 从最远的祖先往下逐级覆盖：子显式写了的字段赢，没写的沿用父。
        var merged = chain[^1];
        for (var index = chain.Count - 2; index >= 0; index--)
        {
            merged = MergeDefinition(merged, chain[index]);
        }

        return merged with { Extends = null };
    }

    /// <summary>把子定义覆盖到父定义上。合并语义（逐条可核对）：</summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>Id</c> 永远用子自己的；<c>Name</c>/<c>Applies-to</c> 是必备身份字段，不继承。</item>
    /// <item>别名（<c>keywords</c>）不继承：父定义仍在同一文件里，继承必然与父重复占用关键字；
    /// 子要别名必须自己声明。<c>&lt;content&gt;</c>、<c>&lt;structure&gt;</c>：子声明则整体覆盖。</item>
    /// <item><c>render</c>/<c>structure-key</c>/<c>selector</c>/<c>variant</c>/<c>item-marker</c>/
    /// <c>opens</c>/<c>fence-char(s)</c>/<c>fence-length</c>/<c>priority</c>：子写了该属性才覆盖，否则沿用父的<b>有效值</b>
    /// （父值可能来自缺省，如围栏字符默认 <c>:</c>）。</item>
    /// <item>属性声明按 key 合并：父全部保留，子同 key 在父的位置上替换，新 key 追加；不允许删除继承来的属性。</item>
    /// <item><c>&lt;uses&gt;</c>：父引用在前、子引用在后，去重保序（追加语义）。</item>
    /// <item>部件上的字段按“源 XML 是否出现该属性”判断，不按“值是否为空/默认”判断
    /// （例如 <c>addressable="false"</c> 必须能覆盖父的 <c>true</c>）。</item>
    /// <item>空值规则：除 <c>tag</c> 外，子显式写空字符串一律视为“没写”，沿用父值——
    /// 删除语义不开放，作者清空一个字段不会静默抹掉父值；<c>tag=""</c> 是“透明部件”的真实取值，允许覆盖。</item>
    /// </list>
    /// </remarks>
    private static UserBlockDefinition MergeDefinition(UserBlockDefinition parent, UserBlockDefinition child)
    {
        var declared = child.DeclaredFields;
        bool Declared(string field) => declared is null || declared.Contains(field);

        // fence-char 与 fence-chars 是同一件事的两种写法：任一出现即视为子声明了围栏字符。
        var fenceDeclared = Declared("fence-char") || Declared("fence-chars");

        return child with
        {
            Id = child.Id,
            Name = !string.IsNullOrEmpty(child.Name) ? child.Name : parent.Name,
            AppliesTo = child.AppliesTo,
            // 别名不继承：父定义仍在同一文件里持有这些关键字，继承会让父子必然重复占用同一关键字。
            // 子要别名必须自己声明；这是“未写沿用父值”的唯一字段级例外。
            Aliases = child.Aliases,
            RenderEnabled = Declared("render") && child.RenderEnabled is not null
                ? child.RenderEnabled
                : parent.RenderEnabled,
            StructureKey = Declared("structure-key") && !string.IsNullOrEmpty(child.StructureKey)
                ? child.StructureKey
                : parent.StructureKey,
            Selector = Declared("selector") && !string.IsNullOrEmpty(child.Selector)
                ? child.Selector
                : parent.Selector,
            Variant = Declared("variant") && !string.IsNullOrEmpty(child.Variant)
                ? child.Variant
                : parent.Variant,
            ItemMarker = Declared("item-marker") && !string.IsNullOrEmpty(child.ItemMarker)
                ? child.ItemMarker
                : parent.ItemMarker,
            Opens = Declared("opens") && !string.IsNullOrEmpty(child.Opens) ? child.Opens : parent.Opens,
            FenceChar = fenceDeclared ? child.FenceChar : parent.FenceChar,
            FenceChars = fenceDeclared ? child.FenceChars : parent.FenceChars,
            FenceLength = Declared("fence-length") ? child.FenceLength : parent.FenceLength,
            Priority = Declared("priority") ? child.Priority : parent.Priority,
            Attributes = MergeAttributes(parent.Attributes, child.Attributes),
            AttributeGroups = MergeOrderedIds(parent.AttributeGroups, child.AttributeGroups),
            Content = Declared("content") ? child.Content : parent.Content,
            Structure = Declared("structure")
                ? MergeStructure(parent.Structure, child.Structure)
                : parent.Structure,
            // 结构图跟着结构走：子自己声明了结构就不再背负父的结构图（两者会指向不同部件树）。
            StructureGraph = Declared("structure") ? child.StructureGraph : parent.StructureGraph,
            // 白名单整节替换：声明了 <inline-html> 就用自己的，否则沿用父的。
            InlineHtml = Declared("inline-html") && child.InlineHtml is not null
                ? child.InlineHtml
                : parent.InlineHtml,
            Extends = null,
        };
    }

    /// <summary>属性声明按 key 合并：父序保留、子同 key 原地替换、子新 key 追加。</summary>
    private static IReadOnlyList<UserBlockAttributeDeclaration> MergeAttributes(
        IReadOnlyList<UserBlockAttributeDeclaration> parent,
        IReadOnlyList<UserBlockAttributeDeclaration> child)
    {
        if (child.Count == 0) return parent;
        if (parent.Count == 0) return child;

        var merged = new List<UserBlockAttributeDeclaration>(parent.Count + child.Count);
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in parent)
        {
            positions.TryAdd(attribute.Key, merged.Count);
            merged.Add(attribute);
        }

        foreach (var attribute in child)
        {
            if (positions.TryGetValue(attribute.Key, out var position))
            {
                merged[position] = attribute;
            }
            else
            {
                positions[attribute.Key] = merged.Count;
                merged.Add(attribute);
            }
        }

        return merged;
    }

    /// <summary>有序 Id 列表追加合并：父在前、子在後，去重保序（<c>&lt;uses&gt;</c> 与 <c>slots</c> 共用）。</summary>
    private static IReadOnlyList<string> MergeOrderedIds(
        IReadOnlyList<string> parent,
        IReadOnlyList<string> child)
    {
        if (child.Count == 0) return parent;
        if (parent.Count == 0) return child;

        var merged = new List<string>(parent.Count + child.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in parent)
        {
            if (seen.Add(id)) merged.Add(id);
        }

        foreach (var id in child)
        {
            if (seen.Add(id)) merged.Add(id);
        }

        return merged;
    }

    /// <summary>结构合并：slots 取并集（父序在前），根部件按同层级同槽位递归合并；父无根时直接用子根。</summary>
    private static UserBlockStructure MergeStructure(UserBlockStructure parent, UserBlockStructure child)
    {
        if (child.Root is null) return parent;
        if (parent.Root is null) return child;

        return new UserBlockStructure
        {
            Root = MergePart(parent.Root, child.Root),
            Slots = MergeOrderedIds(parent.Slots, child.Slots),
            AllowTransparentParts = parent.AllowTransparentParts || child.AllowTransparentParts,
        };
    }

    /// <summary>
    /// 同一部件上的字段级合并：匹配键是槽位 token（<c>UserBlockPart.Part</c>），
    /// 子显式写了某个 XML 属性才覆盖该字段，未写就沿用父值（含布尔字段的 false 覆盖 true）。
    /// </summary>
    private static UserBlockPart MergePart(UserBlockPart parent, UserBlockPart child)
    {
        var declared = child.DeclaredFields;
        bool Declared(string field) => declared is null || declared.Contains(field);

        // 空值规则：除 tag 外，子显式写空字符串视为“没写”，沿用父值。
        string? Inherit(string field, string? childValue, string? parentValue) =>
            Declared(field) && !string.IsNullOrEmpty(childValue) ? childValue : parentValue;

        return child with
        {
            Part = child.Part,
            // tag="" 是透明部件的真实取值，允许把父的 tag 覆盖为 null。
            Tag = Declared("tag") ? child.Tag : parent.Tag,
            Addressable = Declared("addressable") ? child.Addressable : parent.Addressable,
            When = Inherit("when", child.When, parent.When),
            WhenSource = Inherit("when-source", child.WhenSource, parent.WhenSource),
            Repeat = Inherit("repeat", child.Repeat, parent.Repeat),
            RepeatMax = Declared("repeat-max") ? child.RepeatMax : parent.RepeatMax,
            Generator = Inherit("generator", child.Generator, parent.Generator),
            GeneratorParams = Inherit("generator-params", child.GeneratorParams, parent.GeneratorParams),
            Ref = Inherit("ref", child.Ref, parent.Ref),
            Policy = Inherit("policy", child.Policy, parent.Policy),
            Raw = Declared("raw") ? child.Raw : parent.Raw,
            Text = Inherit("text", child.Text, parent.Text),
            Href = Inherit("href", child.Href, parent.Href),
            Src = Inherit("src", child.Src, parent.Src),
            Class = Inherit("class", child.Class, parent.Class),
            Style = Inherit("style", child.Style, parent.Style),
            Content = Inherit("content", child.Content, parent.Content),
            Css = Inherit("css", child.Css, parent.Css),
            ClassRecipe = Inherit("class-recipe", child.ClassRecipe, parent.ClassRecipe),
            TagCondition = Inherit("tag-if", child.TagCondition, parent.TagCondition),
            Role = Inherit("role", child.Role, parent.Role),
            AttributesAfterIdentity = Declared("attrs-after-identity")
                ? child.AttributesAfterIdentity
                : parent.AttributesAfterIdentity,
            SourceCondition = Inherit("source-condition", child.SourceCondition, parent.SourceCondition),
            ChildCollection = Inherit("child-collection", child.ChildCollection, parent.ChildCollection),
            EmitWhenCollection = Inherit("emit-when-collection", child.EmitWhenCollection, parent.EmitWhenCollection),
            EmptyCollection = Inherit("empty-collection", child.EmptyCollection, parent.EmptyCollection),
            EmitNodeIdentity = Declared("no-identity") ? child.EmitNodeIdentity : parent.EmitNodeIdentity,
            SelfClosing = Declared("self-closing") ? child.SelfClosing : parent.SelfClosing,
            SuppressNodeStyle = Declared("suppress-style") ? child.SuppressNodeStyle : parent.SuppressNodeStyle,
            EmitNodeStyle = Declared("emit-node-style") ? child.EmitNodeStyle : parent.EmitNodeStyle,
            EmitNone = Declared("emit") ? child.EmitNone : parent.EmitNone,
            Attributes = MergeBindings(parent.Attributes, child.Attributes),
            Children = MergeChildren(parent.Children, child.Children),
        };
    }

    /// <summary>部件内 <c>attr</c> 绑定按绑定名合并：同名子覆盖、新名追加。</summary>
    private static IReadOnlyList<UserBlockPartAttribute> MergeBindings(
        IReadOnlyList<UserBlockPartAttribute> parent,
        IReadOnlyList<UserBlockPartAttribute> child)
    {
        if (child.Count == 0) return parent;
        if (parent.Count == 0) return child;

        var merged = new List<UserBlockPartAttribute>(parent.Count + child.Count);
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in parent)
        {
            positions.TryAdd(binding.Name, merged.Count);
            merged.Add(binding);
        }

        foreach (var binding in child)
        {
            if (positions.TryGetValue(binding.Name, out var position))
            {
                merged[position] = binding;
            }
            else
            {
                positions[binding.Name] = merged.Count;
                merged.Add(binding);
            }
        }

        return merged;
    }

    /// <summary>
    /// 子部件列表：父的全部部件按原顺序保留，被同槽位子部件覆盖的原地替换，
    /// 子新增的按子顺序追加到末尾；<c>include</c> 占位不参与匹配、永远追加（留给后续片段展开）。
    /// </summary>
    private static IReadOnlyList<UserBlockPart> MergeChildren(
        IReadOnlyList<UserBlockPart> parent,
        IReadOnlyList<UserBlockPart> child)
    {
        if (child.Count == 0) return parent;

        var merged = new List<UserBlockPart>(parent.Count + child.Count);
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parent)
        {
            if (part.Include is null) positions.TryAdd(part.Part, merged.Count);
            merged.Add(part);
        }

        foreach (var part in child)
        {
            if (part.Include is not null)
            {
                merged.Add(part);
                continue;
            }

            if (positions.TryGetValue(part.Part, out var position))
            {
                merged[position] = MergePart(merged[position], part);
            }
            else
            {
                positions[part.Part] = merged.Count;
                merged.Add(part);
            }
        }

        return merged;
    }

    private static IEnumerable<string> EnumerateIncludeIds(UserBlockPart? part)
    {
        if (part is null) yield break;
        if (part.Include is not null) yield return part.Include;

        foreach (var child in part.Children)
        {
            foreach (var id in EnumerateIncludeIds(child))
            {
                yield return id;
            }
        }
    }

    private static void CollectIncludeIds(
        UserBlockPart? part,
        IReadOnlyDictionary<string, UserBlockStructureFragmentDefinition> fragments,
        HashSet<string> seen,
        List<string> ordered,
        int depth)
    {
        if (part is null || depth > MaxExpansionDepth) return;

        if (part.Include is { } includeId)
        {
            if (seen.Add(includeId)) ordered.Add(includeId);
            if (fragments.TryGetValue(includeId, out var fragment))
            {
                CollectIncludeIds(fragment.Root, fragments, seen, ordered, depth + 1);
            }

            return;
        }

        foreach (var child in part.Children)
        {
            CollectIncludeIds(child, fragments, seen, ordered, depth);
        }
    }

    private static IEnumerable<string> EnumerateAttributeKeys(
        UserBlockDefinition definition,
        UserBlockComposition composition)
    {
        foreach (var attribute in definition.Attributes)
        {
            yield return attribute.Key;
        }

        foreach (var id in definition.AttributeGroups)
        {
            var group = composition.AttributeGroups.FirstOrDefault(item =>
                string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (group is null) continue;

            foreach (var attribute in group.Attributes)
            {
                yield return attribute.Key;
            }
        }
    }

    private static void DetectCycle(
        string fragmentId,
        UserBlockStructureFragmentDefinition fragment,
        IReadOnlyDictionary<string, UserBlockStructureFragmentDefinition> fragments,
        List<string> path,
        Action<string, string> report)
    {
        if (path.Contains(fragmentId, StringComparer.OrdinalIgnoreCase))
        {
            var chain = string.Join(" → ", path.Append(fragmentId));
            report(CycleCode, $"结构片段引用成环：{chain}。环上的定义无法展开，必须拆开。");
            return;
        }

        path.Add(fragmentId);
        foreach (var childId in EnumerateIncludeIds(fragment.Root))
        {
            if (!fragments.TryGetValue(childId, out var child) || child.Root is null) continue;
            DetectCycle(childId, child, fragments, path, report);
        }

        path.RemoveAt(path.Count - 1);
    }

    private static void CollectCollisions(
        UserBlockPart part,
        HashSet<string> slots,
        Action<string, string> report)
    {
        if (part.Part != UserBlockStructureRules.WrapperPart && !slots.Add(part.Part))
        {
            report(
                PartCollisionCode,
                $"展开后部件槽位 '{part.Part}' 出现多次：引用片段与自有结构（或两个片段）声明了同一个部件名。");
        }

        foreach (var child in part.Children)
        {
            CollectCollisions(child, slots, report);
        }
    }

    private static UserBlockPart? ExpandPart(
        UserBlockPart? part,
        IReadOnlyDictionary<string, UserBlockStructureFragmentDefinition> fragments,
        int depth)
    {
        if (part is null || depth > MaxExpansionDepth) return part;

        if (part.Include is { } includeId)
        {
            // 缺引用/环已在 Validate 拒绝；这里只做防御性跳过，不产生半展开结构。
            return fragments.TryGetValue(includeId, out var fragment) && fragment.Root is not null
                ? ExpandPart(fragment.Root, fragments, depth + 1)
                : part;
        }

        if (part.Children.Count == 0) return part;

        var children = new List<UserBlockPart>(part.Children.Count);
        foreach (var child in part.Children)
        {
            var expanded = ExpandPart(child, fragments, depth);
            if (expanded is not null) children.Add(expanded);
        }

        return part with { Children = children };
    }
}
