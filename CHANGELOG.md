# Changelog

All notable changes to this package are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0]

### Added
- **Proportional Edit** window (`Tools > Lumi Mesh Tools > Proportional Edit`): grab part of a
  mesh, move, turn or scale it, and let the surrounding surface follow by a falloff — Blender's
  proportional editing, for the garment that is simply worn crooked.
  - Selection by open edge loop (a hem, collar or cuff — click it in the scene), by connected
    shell, or with a brush.
  - **Offset** holds the correction at full strength for a distance before the fade starts.
    Without it the surface just outside the selection barely moves while the selection moves
    fully, and that step shows up as a crease along the boundary.
  - Distance measured along the surface rather than through space, so an edit on a hem does not
    reach the far side of the skirt, plus a stitch distance that bridges the lace, charms and
    straps that sit on a garment as separate shells — without it they get left behind.
  - *Level the selection* fits a plane to a ring and turns it square to the nearest axis.
  - Live preview, Apply/Cancel/Undo per edit, and bake to a `.asset` mesh. Blend shapes, UVs,
    bind poses and every other channel are carried over untouched.

### Added
- **Band** selection: everything past a cut line. Click the model where the crooked part starts,
  or drag the slider. The direction is named in world terms (up/down, left/right, front/back) and
  converted into the mesh's own space, because garments are modelled in every orientation going.
  The existing modes could not express "the top of this garment": a hem with no closed ring has
  nothing for Loop to grab, Island takes the whole piece, and Brush means painting a band by hand.
- **Fit to body** in Proportional Edit. Point it at the avatar's body mesh and it solves for the
  rotation and offset that put the selection back where it belongs, then feeds that through the
  ordinary falloff.
  - The mirror plane comes from the body, not the garment. A garment is rarely symmetric enough to
    say where its own centre is — one measured here was 22mm out at the median — while the body
    under it fitted to 0.00mm, so that is what the plane is taken from. Direction comes from the
    armature, exact position from the body mesh.
  - Two things are balanced: how far the garment is from symmetric, and how evenly it sits off the
    skin. Symmetry alone will happily lift a waistband off the hips to win a millimetre; fit alone
    is blind, because a band can slide right round the body and still hug it everywhere.
  - Measured in bind pose. A posed body carries the pose's own asymmetry — 5.1mm on a test avatar
    against 0.15mm in bind pose — which would poison the reference.
  - Residuals are capped, so trim that is asymmetric on purpose cannot drag the fit. Nothing is
    replaced: every piece keeps its own shape and is simply carried along.
- **Rigid trim.** Lace, buckles and charms sit on a garment as separate shells, and a falloff
  fading across one stretches it — a metal ring is not supposed to bend. Each such shell now takes
  the average influence over its own vertices and moves by that much of the motion, rigidly.

### Fixed
- Both windows kept re-reading the mesh they were previewing. A live preview swaps a throwaway
  copy onto the renderer, and the windows asked the renderer for "the" mesh every frame — so the
  preview came back in as the source, no longer matched the mesh they had snapshotted, and
  triggered a full reset on every repaint. In Proportional Edit that cleared the selection one
  frame after it was made, so nothing could ever be moved and Bake wrote the source out unchanged.
  In Symmetrize it cleared the kept-as-is regions and re-snapshotted the already-symmetrized
  result, so each preview compounded on the last. While a preview is live the source is now the
  mesh the window started from, and a preview mesh is never accepted as a source.
- Vertex colours are written back at the width the source used. Rebuilding an 8-bit colour
  channel as floats changes the vertex stride, and a Skinned Mesh Renderer handed a stride it
  did not expect stops drawing the mesh entirely.
- Swapping a mesh on a Skinned Mesh Renderer clears the old reference first, so the renderer
  rebuilds its skinning setup. Assigning straight over the top leaves it sized for the previous
  vertex count and the garment goes invisible.
- The rebuild now checks its output against the source's vertex layout and reports any channel
  that came out a different width.

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
