# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0]

### Added
- **Symmetrize** window (`Tools > Lumi Mesh Tools > Symmetrize`): rebuilds a mesh so one
  half is an exact mirror of the other.
  - Automatic detection of the mirror axis and plane offset, with a confidence score.
  - Triangles that straddle the mirror plane are cut, so the source half does not need a
    pre-existing seam down the middle.
  - Seam vertices are welded and flattened onto the plane, giving a watertight join.
  - Blend shapes are rebuilt, including multi-frame shapes. Shapes whose names form an
    L/R pair are matched up so a one-sided shape stays one-sided.
  - Bone weights are remapped through L/R bone name matching.
  - UVs (all 8 channels, 2/3/4 components), vertex colours, submeshes and bind poses are
    preserved.
  - Scene view plane gizmo, in-place preview, and bake to a `.asset` mesh with undo.
