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
- **Regions.** The mesh is split into connected shells, and any of them can be marked to keep
  as-is: those are copied through untouched rather than cut and mirrored, so a piece that is
  one-sided on purpose survives. Pick them from the list or by clicking them in the scene
  view; *Score* and *Auto* find the one-sided ones for you.
- **Free mirror plane.** The plane no longer has to be axis aligned, so a piece modelled at an
  angle can be mirrored about the plane it was actually built around instead of being folded
  into a ridge. Place it with scene handles, or let *Fit to mesh* find it — the fit takes its
  candidates from the mesh's principal axes, searches offset and tilt, and reports how much of
  the mesh has a mirror partner. It looks only at the regions being mirrored.
- **Seam relax.** A proportional-editing style pass around the join, with Blender's falloff
  curves (Smooth, Sphere, Root, Linear, Sharp, Constant) measured outwards from the mirror
  plane. This flattens the ridge that appears when the kept half meets the plane at an angle.
  Normals are recalculated inside the band only, leaving hand-authored shading elsewhere
  alone, and regions kept as-is are excluded from it.
