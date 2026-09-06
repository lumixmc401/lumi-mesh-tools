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
2. **Press *Detect automatically*.** It scores all three axes and reports how much of the
   mesh already has a mirror partner. On a VRChat avatar the answer is almost always
   *X at 0*. A low score is expected for a mesh that genuinely is asymmetric — that is what
   you are here to fix — so check the blue plane gizmo in the scene view rather than
   trusting the number.
3. **Choose the half to keep.** For an avatar mesh, `−X` is the avatar's *left* and `+X` is
   its *right*. The half you keep is mirrored onto the other one.
4. **Press *Preview*** to see the result in the scene. Nothing is written to disk yet, and
   closing the window puts the original mesh back.
5. **Press *Bake & Apply*.** The result is saved as a `.asset` mesh under
   `Assets/LumiMeshTools/Generated` and assigned to the renderer. The assignment is
   undoable; the saved asset stays on disk.

The original FBX is never modified.

### What survives the rebuild

| | |
|---|---|
| Blend shapes | Rebuilt, including multi-frame shapes and delta normals/tangents. Shapes whose names form an L/R pair (`Wing_L` / `Wing_R`, `Left eye` / `Right eye`) are matched up, so a one-sided shape stays one-sided instead of firing on both halves. |
| Bone weights | Remapped through L/R bone name matching, so the mirrored half is driven by the mirrored bones. |
| UVs | All eight channels, at their original 2/3/4 component width. Both halves share the source half's UV island, which is what makes a symmetric texture line up. |
| Vertex colours, submeshes, bind poses | Preserved. |

### Options

- **Weld seam** — share the vertices that land on the plane between both halves, so the
  join is watertight and shades continuously. Leave this on unless you have a reason.
- **Seam tolerance** — how close to the plane a vertex has to be to count as sitting on it.
  *Detect automatically* sets a sensible value from the mesh size.
- **Pair L/R blend shapes** — see above. Turn it off to mirror every shape from its own
  deltas.
- **Mirror bone weights** — turn off to leave the mirrored half weighted to the original
  bones (useful when a garment is rigged to a single chain rather than an L/R pair).

### How it works

Three stages. First the mesh is **cut** against the mirror plane: triangles that straddle it
are split, with every per-vertex channel interpolated across the cut, so the half you keep
does not need a pre-existing seam down the middle. Then the vertices that landed on the
plane are **welded** and flattened onto it. Finally the survivors are **duplicated
reflected** across the plane, with winding order reversed, normals and tangents flipped, and
bone indices remapped.

### Known limits

- Triangle meshes only. Quad or line topology is rejected rather than mangled.
- Bone weights on the seam row are kept as they were rather than symmetrised, so a seam
  vertex weighted to a one-sided bone stays weighted to it.
- The mirror plane is axis-aligned in the mesh's own space. A garment authored at an angle
  needs its transform straightened first.
- The mirrored half reuses the source half's UVs. If the two halves were meant to have
  different textures, this is not the tool for that.

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
2. **按 Detect automatically** — 自動判斷鏡像軸與平面位置,並回報「已經有多少比例的頂點找得到鏡像
   對應」。VRChat 角色幾乎都是 *X 軸、位置 0*。分數低是正常的(本來就不對稱才要修),重點是看
   Scene 視窗裡那片藍色平面對不對。
3. **選要保留哪一半** — 角色的 `−X` 是**左邊**,`+X` 是**右邊**。保留的那半會被鏡射到另一半。
4. **按 Preview** 在場景裡直接看結果,此時還沒寫入任何檔案,關掉視窗會還原。
5. **按 Bake & Apply** — 產生的 mesh 存到 `Assets/LumiMeshTools/Generated`,並指派給 renderer。
   指派這個動作可以 Ctrl+Z 復原,存下來的 `.asset` 會留著。

原始 FBX 完全不會被改到。

BlendShape(含多 frame、含 L/R 成對的單側 shape)、骨骼權重(依 L/R 骨名對應重新指派)、
8 組 UV、頂點色、submesh、bindpose 都會保留。

已知限制:只支援三角面;接縫那一排頂點的權重維持原樣不做對稱化;鏡像平面必須對齊 mesh 自身空間的
座標軸(斜著做的衣服要先把 transform 擺正);鏡射出來的那半共用原本那半的 UV。

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
