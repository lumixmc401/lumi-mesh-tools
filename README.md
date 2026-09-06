# Lumi Mesh Tools

Small editor tools for fixing up meshes inside Unity, without a round trip through Blender.

The first one is **Symmetrize**: rebuild a mesh so that one half is an exact mirror of the
other. It exists because bought avatar outfits are often *nearly* symmetric — one sleeve
sits a millimetre off, a ribbon is only on one side, a collar is subtly lopsided — and
fixing that shouldn't mean exporting the FBX, repairing it in Blender, re-importing, and
reassigning every material and blend shape by hand.

> Status: early. Tested on triangle meshes with blend shapes and skinning. Please keep a
> backup of your scene until you have seen it work on your own assets.

---

## Install

### Via VCC / ALCOM (recommended)

1. Add the listing: <https://lumixmc401.github.io/lumi-vpm/>
2. Find **Lumi Mesh Tools** in your project's package list and press **+**.

### Via Unity Package Manager

*Window → Package Manager → + → Add package from git URL:*

```
https://github.com/lumixmc401/lumi-mesh-tools.git
```

Append `#v0.1.0` to pin a specific version.

---

## Using Symmetrize

Open **Tools → Lumi Mesh Tools → Symmetrize**.

1. **Pick the renderer.** Selecting a Skinned Mesh Renderer or Mesh Renderer in the
   hierarchy fills it in for you.
2. **Press *Fit to mesh*.** It scores all three axes and reports how much of the
   mesh already has a mirror partner. On a VRChat avatar the answer is almost always
   *X at 0*. A low score is expected for a mesh that genuinely is asymmetric — that is what
   you are here to fix — so check the blue plane gizmo in the scene view rather than
   trusting the number.
3. **Choose the half to keep.** For an avatar mesh, `−X` is the avatar's *left* and `+X` is
   its *right*. The half you keep is mirrored onto the other one.
4. **Set aside anything that is one-sided on purpose.** Under *Regions*, untick the shells you
   want left exactly as they are — a bag on one shoulder, a ribbon down one side. *Pick in
   scene* lets you click them on the model instead of hunting through the list, and *Score*
   followed by *Auto* marks the pieces that have almost no mirror partner.
5. **Press *Preview*** to see the result in the scene. Nothing is written to disk yet, and
   closing the window puts the original mesh back.
6. **Press *Bake & Apply*.** The result is saved as a `.asset` mesh under
   `Assets/LumiMeshTools/Generated` and assigned to the renderer. The assignment is
   undoable; the saved asset stays on disk.

The original FBX is never modified.

### When the middle comes out as a ridge

Mirroring is exact, so if the half you kept arrives at the plane at an angle, its reflection
leaves at the opposite angle and the two meet in a crease down the centre. There are three
causes, and the fix differs:

- **The plane is slightly off centre.** *Fit to mesh* searches the offset, not just the axis.
- **The piece was modelled at an angle**, so the axis plane is not the plane it is symmetric
  about. *Fit to mesh* searches the tilt too, and switches to a free plane when it finds one.
  Its candidates come from the mesh's principal axes, which is exact for a symmetric shape:
  reflection commutes with the covariance matrix, so the plane's normal has to be one of them.
- **The kept half genuinely is not square to the plane.** Nothing can mirror that without a
  crease, so relax it — set a *Radius* under *Seam* and pick a *Falloff*. It works like
  Blender's proportional editing, with the mirror plane as the centre the influence radiates
  out from, and it does what you would otherwise do by hand.

A piece that is both tilted and one-sided takes two passes: keep everything else as-is, fit
the plane to just that piece and mirror it, then swap which regions are active.

### What survives the rebuild

| | |
|---|---|
| Blend shapes | Rebuilt, including multi-frame shapes and delta normals/tangents. Shapes whose names form an L/R pair (`Wing_L` / `Wing_R`, `Left eye` / `Right eye`) are matched up, so a one-sided shape stays one-sided instead of firing on both halves. |
| Bone weights | Remapped through L/R bone name matching, so the mirrored half is driven by the mirrored bones. |
| UVs | All eight channels, at their original 2/3/4 component width. Both halves share the source half's UV island, which is what makes a symmetric texture line up. |
| Vertex colours, submeshes, bind poses | Preserved. |

### Options

- **Weld** — share the vertices that land on the plane between both halves, so the join is
  watertight and shades continuously. Leave this on unless you have a reason.
- **Tolerance** — how close to the plane a vertex has to be to count as sitting on it.
  *Fit to mesh* sets a sensible value from the mesh size.
- **Radius / Falloff / Strength / Iterations** — the seam relax. A radius of zero turns it off.
- **Fix normals** — recalculate normals inside the relaxed band and blend them in by the same
  falloff, so shading follows the geometry there while the rest of the mesh keeps its own.
- **Pair L/R blend shapes** — see above. Turn it off to mirror every shape from its own
  deltas.
- **Mirror bone weights** — turn off to leave the mirrored half weighted to the original
  bones (useful when a garment is rigged to a single chain rather than an L/R pair).

### How it works

Four stages. First the mesh is **cut** against the mirror plane: triangles that straddle it
are split, with every per-vertex channel interpolated across the cut, so the half you keep
does not need a pre-existing seam down the middle. Then the vertices that landed on the plane
are **welded** and flattened onto it. The survivors are **duplicated reflected** across the
plane, with winding order reversed, normals and tangents flipped, and bone indices remapped.
Finally the band around the join is **relaxed**, if you asked for it.

Regions marked to keep as-is skip all four stages and are copied straight through.

### Known limits

- Triangle meshes only. Quad or line topology is rejected rather than mangled.
- Bone weights on the seam row are kept as they were rather than symmetrised, so a seam
  vertex weighted to a one-sided bone stays weighted to it.
- The mirrored half reuses the source half's UVs. If the two halves were meant to have
  different textures, this is not the tool for that.
- Regions are whole connected shells. A one-sided detail that shares its geometry with the
  rest of the garment cannot be set aside on its own.
- *Fit to mesh* copes with a modest one-sided piece, but a very large one drags the estimate
  off. Set those regions aside first and fit again — the fit only looks at what is being
  mirrored.
- The seam relax moves vertices, so it does change the shape near the join. That is the point,
  but blend shape deltas there were authored for slightly different positions.
- Picking in the scene view tests the mesh in its own space, so on a skinned mesh posed away
  from its bind pose the click lands where the mesh *was*, not where it is drawn.

---

## 中文說明

把 Unity 裡的 mesh 直接修一修的小工具,不用再繞去 Blender。

第一個功能是 **Symmetrize(對稱化)**:指定某一側,把它鏡射到另一側,讓整個 mesh 變成左右對稱。
會做這個是因為買來的衣服常常「差一點點對稱」——袖子偏個一毫米、緞帶只有一邊有、領口有點歪——
而為了這種小事把 FBX 匯出去 Blender 修好再匯回來,材質和 BlendShape 全部要重指定,太不划算。

### 安裝

**VCC / ALCOM**:加入 listing <https://lumixmc401.github.io/lumi-vpm/>,然後在套件列表裡按 **+**。

**Unity Package Manager**:`Window → Package Manager → + → Add package from git URL`,
貼上 `https://github.com/lumixmc401/lumi-mesh-tools.git`。

### 用法

開 **Tools → Lumi Mesh Tools → Symmetrize**。

1. **選 renderer** — 在 Hierarchy 點選物件會自動帶入。
2. **按 Fit to mesh** — 自動判斷鏡像平面(含位置與傾角),並回報「已經有多少比例的頂點找得到鏡像
   對應」。VRChat 角色幾乎都是 *X 軸、位置 0*。分數低是正常的(本來就不對稱才要修),重點是看
   Scene 視窗裡那片藍色平面對不對。
3. **選要保留哪一半** — 角色的 `−X` 是**左邊**,`+X` 是**右邊**。保留的那半會被鏡射到另一半。
4. **把刻意單邊的部件設成保留**(見下方說明)。
5. **按 Preview** 在場景裡直接看結果,此時還沒寫入任何檔案,關掉視窗會還原。
6. **按 Bake & Apply** — 產生的 mesh 存到 `Assets/LumiMeshTools/Generated`,並指派給 renderer。
   指派這個動作可以 Ctrl+Z 復原,存下來的 `.asset` 會留著。

原始 FBX 完全不會被改到。

BlendShape(含多 frame、含 L/R 成對的單側 shape)、骨骼權重(依 L/R 骨名對應重新指派)、
8 組 UV、頂點色、submesh、bindpose 都會保留。

**刻意不對稱的部件**:在 *Regions* 把那幾塊的勾取消,它們就會原封不動複製過去,不切也不鏡射。
可以按 *Pick in scene* 直接在場景裡點模型來選,或按 *Score* 再按 *Auto* 讓它自動找出單邊的部件。

**中間出現稜線**:鏡射是精確的,所以保留的那半如果是斜著碰到平面,鏡射過去就會把斜率翻倍成一道稜線。
三種成因對應三種解法——平面位置差一點(*Fit to mesh* 會搜尋位置)、部件本身是斜的(*Fit to mesh*
連傾角一起找,找到就自動切成自由平面)、來源那半本來就不方正(在 *Seam* 設一個 *Radius* 並選
*Falloff* 曲線,以鏡像平面為中心做比例衰減的鬆弛,就是 Blender 的 Proportional Editing 那一套)。

又斜又單邊的部件分兩次做:先把其他區塊全部設成保留、只對它擬合平面並鏡射,再把啟用的區塊反過來。

已知限制:只支援三角面;接縫那排頂點的權重維持原樣不做對稱化;鏡射出來的那半共用原本那半的 UV;
區塊是以「連通的殼」為單位,和主體連在一起的單邊細節沒辦法單獨挑出來;*Fit to mesh* 遇到非常大的
單邊部件會被拉偏,先把它設成保留再擬合即可;接縫鬆弛會實際移動頂點,所以那一帶的形狀會改變;在場景裡
點選是用 mesh 自身空間去測,骨架擺成非 bind pose 時,點擊會對應到它「原本」的位置。

---

## Development

The package is the repository root, so it can be dropped straight into a project's
`Packages/` folder, referenced with `"com.lumixmc401.meshtools": "file:<path>"` from
`Packages/manifest.json`, or pulled in by git URL.

Tests live in `Tests/Editor` and run from Unity's Test Runner in Edit Mode.

Releasing: bump `version` in `package.json`, add the matching section to `CHANGELOG.md`,
then push a `v<version>` tag. CI checks the two agree, builds the zip, and publishes a
GitHub release that the [VPM listing](https://github.com/lumixmc401/lumi-vpm) picks up.

## Licence

MIT — see [LICENSE.md](LICENSE.md).
