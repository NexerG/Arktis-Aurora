---
name: aurora-code
description: How to write C# in Aurora — performance first; allocation, collections, LINQ, structs and classes, data pools, loops, naming, formatting, comments. Load before writing or editing any C# in this repo, including tests, tooling and one-line fixes.
---

# Writing C# in Aurora

Every rule here is hard. Where a rule has a boundary, the rule states it. New code follows this skill even
where the file around it doesn't; existing code changes only when the change rewrites that statement anyway.

The system facts these rules lean on stay in CLAUDE.md, which is always in context: Rendering (mapped
buffers), Logging, Threading, XSD. The evidence and measurements behind the rules are in
`DOCUMENTATION/ClaudeMemory/Decisions/aurora-code-rules.md`.

## Scope of a change
- Performance is the priority. When it pulls against brevity or readability, performance wins.
- The frame budget is 3.3 ms (300 fps), and a frame allocates nothing.
- A slow system gets faster, not spread across threads; anything threaded also runs on one core.
- Within that, write the least code that solves the problem. A rule's cost in lines is never a reason to skip it.
- No features beyond what was asked. No abstractions for single-use code. No flexibility or configurability that wasn't requested.
- No error handling for impossible scenarios.
- If it could be a quarter of the length within these rules, rewrite it.

## Allocation
1. Nothing allocates per frame. In code the frame loop reaches: no `new` of a class or array, no string building, no capturing lambda, no boxing, no iterator. Allocate at load or prepare, then reuse.
2. Count, then allocate once. If the size can be computed, compute it first, then `new T[n]`.
3. A buffer reused every frame and private to one step is an instance field of that step's owner, never `static`. Reset it with `Array.Clear` or by resetting its count.
4. Scratch space: `stackalloc` into a `Span<T>` up to 1 KB, never inside a loop; above that, `ArrayPool<T>.Shared.Rent` and `Return` in a `finally`.
5. Results go into a buffer the caller owns — a `Span<T>` and a `ref int` cursor — not a returned collection.
6. GPU-mapped memory is written whole: `Unsafe.Write` or `WriteMappedRange`. Never a field write, `+=`, or a read through the pointer.

## Collections
7. Arrays, not `List<T>`. `List<T>` only when the final count can't be known before filling, and always with a capacity. Exception: child collections of an `[A_XSDType]` are `List<T>` — `XmlReflection` recognises no other collection.
8. No nested collections. `List<List<T>>` or `T[][]` becomes one flat `T[]` and an `int[]` of start offsets; run `k` is `items[starts[k]..starts[k + 1]]`.
9. Grids are flat: `T[w * h]`, indexed `y * w + x`. Never `T[,]` or `T[][]`.
10. Keyed lookups use a `Dictionary` built once; linear search only under 16 items. Struct keys implement `IEquatable<T>` and override `GetHashCode`.
11. A run of items is passed as `ReadOnlySpan<T>`, `Span<T>` or `T[]` — never `IEnumerable<T>`, `IList<T>`, `IReadOnlyList<T>` or `ICollection<T>`.

## Data pools
12. Per-row data that a step loops over every frame lives in a pool; per-object state nothing loops over stays a field. Moving an existing OO component into a pool, or changing the entity/component model, asks first.
13. A pool is declared in `Pools.pools.axml` — `<Pool Name Capacity Ordered Growth GrowthValue Handles>` with one `<Component Type>` per column. XML picks the structs; C# defines their layout.
14. Column structs are unmanaged and attribute-free: no `string`, class, array or delegate fields — another row is referred to by `DataHandle` or an integer id. Serialization attributes stay on the owning class's properties.
15. Behaviour is a step's loop over spans, not a per-row virtual call. Per-row variation is an enum or flag switched in the loop; a few rows are a packed side list (the awake list); variable-length data is a `(start, count)` range into a shared column.
16. A pool is `Handles="false"` unless a row needs an identity that outlives a frame edge. Handle-less: `Append`, `Append(count)`, `Truncate`, `Rewind` — `Append` returns an uncleared row, so every field gets written. With handles: `Allocate`, which clears the row, and `Free`, which only enqueues — the row lives until the edge.
17. A per-frame list that other steps or the renderer read is a handle-less pool, rewound and appended each frame; a list private to one step is rule 3's field.
18. Rows are reached through `GetSpan<T>()`, `GetRef<T>(handle)` and `Write<T>(handle, in value)`, keyed by `ColumnKey<T>` — never a `Dictionary<Type, …>` lookup in a loop.
19. Every write is marked. `Write<T>` marks for you; a write through `GetRef` or a span is followed by `MarkContentDirty(handle)` or `MarkRangeDirty(from, to)`, or no consumer sees it.
20. A span is never stored in a field or held across frames. Fetch it at the top of the step, and again after any call that can add rows to that pool — a row appended after the fetch lies outside it.
21. A row `ref` survives appends but not a capacity grow. Hold one only where nothing in between can grow the pool; layout's ref across child calls is the one accepted exception.
22. Dense indices move at every frame edge. Keep a `DataHandle` across frames, never a dense index, and check `Alive` or `DenseOf` before using a handle that may be stale.
23. Structural change happens only in the pool's `FrameEdge`: frees drain, rows compact, an `Ordered` pool resequences. Never shift rows yourself; an order change is `MarkOrderDirty()`.
24. A step touches only the columns its `<Step>` declares in `Frame.frame.axml` — `Reads=`, `Writes=`, as `Pool` or `Pool.Column`. New access means a new entry there; `AssertAccess` throws in DEBUG otherwise.
25. Another thread reads a pool only through `Backing<T>`, `CopyTo`, `CopyRange`, `OwnerAt`, `Count`, `Capacity` and the versions, and never writes through what they return.
26. A consumer learns of changes by polling the pool's versions through its own `PoolCursor` — one per consumer, one per swapchain image for the renderer. No events, no bus, no shared clearable dirty flag. Read the version first, then the snapshot.
27. A consumer iterates its own provisioned set, never the pool's live set: it creates late and destroys through its own deferred queue.
28. A step's rows are split across workers only through `Jobs.For`, and only once the single-thread loop can't get faster.

## LINQ
29. No LINQ in any new code — runtime, boot, tooling, importers, tests. `ImplicitUsings` imports `System.Linq` everywhere, so the compiler won't flag it; check the diff.

| Instead of | Write |
|---|---|
| `Where`, `Select` | a `for` loop into a buffer |
| `OrderBy` | `span.Sort` with a `static` lambda or a struct comparer |
| `Distinct` | a field-held `HashSet<T>`, cleared between uses |
| `Any`, `First`, `FirstOrDefault` | a loop with an early `return` |
| `Count()` | `.Length` or `.Count` |
| `Sum`, `Min`, `Max` | a loop |
| `ToList`, `ToArray` | count, allocate, fill |

## Types
30. Struct for values, class for identity. Points, ids, handles, ranges, rows, results and keys are structs. A class only for something with a lifetime or identity: a resource owner, a service, a control, a pool, a system. Tooling included.
31. `readonly struct` when the value never changes; otherwise the non-mutating members are `readonly`.
32. Several results come back through `out`, a named struct or a `ValueTuple` — never a class or array allocated to carry them.
33. A struct parameter the method only reads is `in`; one it writes is `ref`. `ref` on a reference type only when the callee replaces the object.
34. Classes are `sealed` unless something derives from them. Data loops (pool columns, math kernels) make no virtual or interface calls: call the concrete type, or constrain a generic `where T : struct, IFoo`. The control tree's virtual `Measure`/`Arrange` is exempt.
35. No boxing: no struct cast to `object` or an interface, no `params object[]` on a hot call.
36. Integer data lives in integer types — lattice coordinates, counts, indices. Floats only for continuous values.
37. When enum order means something, use it: clockwise directions turn right with `(d + 1) & 3` and left with `(d + 3) & 3`.

## Loops
38. Loop to `array.Length` on the array or span itself. Copy an array field to a local before the loop.
39. Hoist anything that doesn't change per iteration: field loads, lookups, derived values.
40. Reject the cheapest case first — O(1) tests before O(n) scans.
41. Shrink the data before the expensive stage; emit only what the next stage needs.
42. A loop whose end depends on data the code doesn't control has an iteration cap that logs where it fired and on what input — through `Log.Once()` inside the frame loop. A loop bounded by construction has no cap.
42a. No `[MethodImpl(MethodImplOptions.AggressiveOptimization)]`. It skips tiered compilation, so the method never gets the PGO recompile; hot code is left to the tiered JIT, and cold-start cost is cut with data and design, not JIT flags. Exception: Arctis.Imaging decode loops, which run once per image (see managed-inflate).

## Naming
43. PascalCase for types, methods, properties and constants; camelCase for fields, public and private, parameters and locals; no underscore prefix. Interfaces `IName`, attributes `A_NameAttribute`, a subsystem's channel `static readonly LogChannel Log`. Win32 and Vulkan constants keep their C header names.
44. The name's form says what kind of method it is. Predicates are questions — `AreCollinear`, `DoesIntersect`, `IsInsidePolygon`, `HasAllNeighbours`, `CanTravelInDirection`. Queries say what they return — `WhereIntersects` (a location) beside `DoesIntersect` (yes or no). Actions are verbs — `Collect`, `FloodFill`, `Triangulate`, `Create`. Call sites read as sentences.
45. Name length tracks how close the code is to a formula. Math uses the textbook's letters — `p1..p4`, `o1..o4`, `d1`, `d2`, `denom`, `t`, `u` — so it checks against the formula symbol by symbol. Control flow uses long names — `startingPoint`, `localForward`, `submeshIndex`, `closestIndex`.
46. One metaphor per algorithm, shared by names and comments. A contour walk is a turtle: `walker`, `forward`, `corner`, relative left / forward / right, turn, go straight. You reason about the turtle, not the indices.
47. A prefix names the index a collection takes: `layerFirstRing[layer]`, `ringStarts[ring]`, `points[i]` — the name says what goes in the brackets before you look at the declaration.

## Formatting
48. Block namespaces `namespace X { … }`, never file-scoped; `using` directives above the namespace.
49. Four spaces, no tabs. Opening braces on their own line.
50. A single-statement body takes no braces; a short one sits on the `if` line — `if (dense < 0) return;`.
51. Explicit types: no `var`, `out Type name` rather than `out var`, `new Type(…)` spelled out.
52. `== null` and `!= null`, not `is null`.
53. A member that is one expression uses `=>`.
54. Entry point first, callees below their callers, primitives last. A long file groups its members with `#region`.
55. No line limit in code.

## Comments
56. No "why" in code. Tradeoffs, rejected alternatives, budgets, consequences and design reasoning go to `DOCUMENTATION/ClaudeMemory/*` and `DOCUMENTATION/Engine*` — never above a method.
57. Fields: one short label over a group of related fields, never one per field.
58. Methods: one very short descriptor, or nothing.
59. Inside a method, only hard loops and genuinely long methods get comments, labelling sections. A straightforward ~20-line method gets nothing.
60. Don't re-comment code you're editing. If a comment must change, it doesn't get longer.
61. Comments wrap at about 110 columns and never leave a short last line.

```csharp
// WRONG - one per field, and a rationale paragraph over the method
public float x;          // horizontal position
public float top;        // top edge
public float height;     // line height

// Re-measures one block and slides everything below it - the typing path, where a keystroke
// changes one paragraph and leaves every other block's lines as they were. The shift is a loop
// rather than a Fenwick tree because a document is a few hundred blocks and the measurement
// that just ran costs more than the additions do.
public void InvalidateBlock(int index)

// RIGHT - one label for the group, one short descriptor for the method
// caret geometry in document space
public float x;
public float top;
public float height;

// Re-measures one block and shifts the tops below it.
public void InvalidateBlock(int index)
```

The test: if the comment explains *why*, it belongs in ClaudeMemory, not the file.

## Reference files — right/wrong pairs, read when working in that area
- `collections.md` — allocation and collections, rules 2–11
- `pools.md` — data pools, rules 16–26
- `types.md` — types, rules 30–35
- `loops.md` — loops, rules 38 and 42
