# Pattern — store data the way it is found, compute it the way it is processed (SIMD layout)

**Date:** 2026-09-23
**Status:** guidance. First users of the all-widths rule: the `Arctis.Imaging` codecs ([[simd-width-tiers]], 2026-11-07). No engine (AuroraEngine) code follows the layout advice yet; first intended user: the animation SIMD rework (track
runner + transforms; user picked both, quaternion rotation, width 8 — 2026-09-23).

SIMD runs one instruction over 8 lanes, and the 8 values must share one register (`vx` of 8 items). AoS vs
SoA vs AoSoA is only the question of how they get there and what that costs.

## The cases

| The data is… | Store it | Compute it | Box2D/3D example |
|---|---|---|---|
| shared by several batches or systems, reached by index | **AoS**, hot/cold split, hot row sized to 16 or 32 B | gather 8 rows → transpose in registers → wide math → scatter what changed | body state |
| private to one batch, reused over several passes | **SoA per batch** (8 lanes per struct), built once in a prepare step, discarded after | load straight, no transpose | wide contact constraint |
| light math, sequential, memory-bound | AoS, packed | **plain scalar loop** — SIMD does not make memory faster | integrate velocities / positions |
| large, dense, same items in the same order every tick, moderate math (enough for SIMD to pay, too little to hide a transpose) | the one case for **stored SoA/AoSoA** — measure the transpose share first | load straight | none — Box never stores it persistently |

- Transposing into SoA registers pays only when the math between gather and scatter outweighs the shuffle.
  Transcendentals (spring `Exp` + `SinCos`, easing) clear that bar easily; a lone lerp may not.
- Keeping data permanently SoA pays only when nothing needs to find it one item at a time.

## Why shared data stays AoS

- Items picked by index are never neighbours. In SoA, 8 of them are 8 scattered reads **per field**; in AoS,
  one or two reads per item fetch everything.
- Shared data needs one canonical home every batch reads and writes, so each sees the previous one's result.
- The transpose is register-only. A 32 B row → 8 loads + an 8×8 transpose (Box2D AVX2).

## Before a wide batch

- **Same kind in every lane.** Bucket or sort by kind first; per-lane branches become masks that compute every
  branch.
- **No two lanes write the same target**, or one write is lost at scatter. Graph colouring (Box3D: 24 colours,
  the last is overflow, solved scalar) or targets unique by construction. Colouring also lets workers split a
  colour without locks.
- **Packed active set.** Move parked items into another set (Box solver sets: awake, static, disabled, one per
  sleeping island) instead of skipping flagged rows.
- **Empty lanes** get identity values built in registers — never a shared dummy row in the array, which threads
  would bounce between cores. Box: base-1 indices, 0 = none, so zeroed memory reads as none.
- **Scatter only what the batch changed.** Box3D writes back velocities only and skips non-dynamic bodies.

## .NET specifics

- **Every width is a target where the work allows it** (user, 2026-11-07): `Vector512` → `Vector256` →
  `Vector128` → scalar, each tier gated on its own `IsHardwareAccelerated` and falling through to the next for
  the tail. A tier is skipped only when the algorithm cannot fill it (a serial dependency, a fixed 8-wide
  block); the note or the code's doc says which.
- An x86 intrinsic tier (`Avx512BW`, `Avx2`) also checks the matching `VectorNNN.IsHardwareAccelerated`, so it
  honours the runtime's choice — .NET reports `Vector512` unaccelerated on CPUs that downclock under it, and
  `DOTNET_PreferredVectorBitWidth` can turn it off.
- Width 8 = `Vector256<float>`. On ARM64 `Vector256.IsHardwareAccelerated` is false → the `Vector128` tier.
- net10 has `Vector256.Exp`, `Sin`, `Cos`, `SinCos`, `Lerp`, `FusedMultiplyAdd` (checked 2026-09-23 against
  `Microsoft.NETCore.App.Ref` 10.0.12).
- A `T[]` is 8/16-byte aligned. Box2D asserts 32-byte alignment for aligned loads; here use `LoadUnsafe`, and
  half the 32 B rows straddle a cache line. `NativeMemory.AlignedAlloc` only if measured — it leaves the
  `PoolColumn<T>` `T[]` model.
- `System.Numerics.Vector3`/`Vector4` are horizontal SIMD (one item's xyz[w] in one register) — right for
  one-off geometry; Box3D's `b3V32` plays the same role.
- Row sizing for transforms: position 12 + quaternion 16 + uniform scale 4 = **32 B**, one `Vector256`.
  Non-uniform scale = 40 B → field-by-field gather (Box3D-style) or scale in its own column. **Open fork.**
- Measure a Release build before and after; profiling an optimized build needs `DEBUG` defined
  ([[engine-profiling]]).

## Determinism (simulation clock only — presentation / UI is exempt)

- No `FusedMultiplyAdd` in a kernel that has a scalar twin. Box's `b3MulAddW` is a multiply then an add so the
  wide path matches the scalar one.
- Own the transcendental approximations and share them between both paths — never `MathF.*` in one and
  `Vector256.*` in the other. Box: `b3ComputeCosSin` (rational), `b3Atan2` (minimax polynomial).
- Pad the last batch instead of a scalar tail, so an item's bits do not depend on its array position.
- Large worlds: Box3D stores world position as doubles (`b3Pos`), keeps quaternions float, solves in float
  position deltas.

## Evidence — Box2D v3 and Box3D (read 2026-09-23)

| | Box2D v3 | Box3D |
|---|---|---|
| hot body row | `b2BodyState` 32 B (v, w, flags, dp, dq as cos/sin), 32-byte aligned, asserted | `b3BodyState` 56 B (v, w, dp, dq quaternion, flags) |
| widths | 4 (SSE2/NEON), 8 (AVX2, opt-in), scalar `struct { float x, y, z, w; }` | 4 only (`_Static_assert(B3_SIMD_WIDTH == 4)`); SSE2, NEON, scalar |
| gather | 8 aligned 256-bit loads + 8×8 in-register transpose | field by field (`b3SetW`) |
| wide | convex contact solver | convex contact solver |
| scalar | integration, overflow contacts | integration, mesh contacts, overflow contacts |

- Wide constraints are rebuilt every step in the prepare stage (`b3ContactConstraintWide`: `b3FloatW` fields for
  4 contacts, `int indexA[4]`/`indexB[4]`), then read by warm start, solve and relax across every substep.
- Measured (Catto, "SIMD Matters", 2024-08; 7950X, 5,050-body pyramid, 4 workers): scalar 1.91 ms, SSE2
  1.02 ms, AVX2 0.90 ms — SSE2 ~2× scalar, AVX2 +14% over SSE2. M2: scalar 1.47 ms, NEON 0.95 ms.
- Box3D ships width 4 only; no reason found in the source or README.

## If stored SoA/AoSoA is ever adopted

- `IPoolColumn` assumes one element per row; a block column needs lane-wise `Move`/`Clear`/`Permute`, and
  `WriteBytes` scattering an AoS payload so commands and XML keep seeing a plain struct.
- Hand access loses `ref`: a `ref struct` accessor gathers `Vector3` from the block (3 loads within 96 B).
- A sparse active subset wastes lanes unless the set is compacted.

## Sources

- https://box2d.org/posts/2024/08/simd-matters/
- https://github.com/erincatto/box3d — `src/simd.h`, `src/contact_solver.c`, `src/body.h`, `src/core.h`,
  `src/solver.c`, `src/math_functions.c`, `include/box3d/math_functions.h`, `include/box3d/constants.h`
- https://github.com/erincatto/box2d — `src/contact_solver.c`, `src/body.h`, `src/core.h`

Related: [[animation-core]], [[ecs-rework-data-pools]], [[engine-profiling]]
