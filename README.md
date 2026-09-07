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

## Using Proportional Edit

Open **Tools → Lumi Mesh Tools → Proportional Edit**.

This is the tool for a garment that is not really asymmetric, just *worn crooked* — a hem, a
collar or a cuff that sits higher on one side than the other. It works the way Blender's
proportional editing does: take hold of part of the mesh, move it, and everything nearby comes
along by an amount that fades with distance.

1. **Select.** *Loop* mode grabs a whole open edge loop when you click near it — a hem, a
   collar, a cuff. *Island* takes a connected piece; *Brush* paints a selection on by hand.
2. **Set the falloff.** *Radius* is how far the influence fades over. **Offset** holds it at
   full strength for that distance first — this is what stops the correction pinching: fading
   straight from full strength at the selection leaves the surface just outside it barely
   moving while the selection moves fully, and that step reads as a crease. Push the offset out
   past where the bend would land and it disappears.
3. **Edit.** Drag the handle, or press *Level the selection* to fit a plane to a ring and turn
   it square. *Apply* commits; *Cancel* backs it out; *Undo* steps back through committed edits.
4. **Bake & Apply** saves the result and assigns it, as with Symmetrize.

**Stitch pieces within** matters more than it sounds. Lace, charms and straps are usually
separate shells laid against a garment rather than joined to it, and measuring distance along
the surface walks straight past them — the garment moves and the trim stays behind, floating.
Anything within the stitch distance of another piece is treated as attached. On a typical
outfit a millimetre or two is enough.

---

## Fitting a garment to the body

Bought clothing is often not worn straight: a waistband rides higher on one side, a hem sits
crooked. The obvious fix — make the garment symmetric — is usually the wrong one, because the
asymmetry you want gone and the asymmetry the designer intended are mixed together in the same
mesh. Mirroring one half onto the other levels the garment and throws the design away with it.

**Fit to body** separates the two. It never replaces geometry: it works out a single rotation and
offset for the part you selected, and applies it through the normal falloff.

1. In **Body reference**, assign the avatar's body mesh — the skin, not clothing. Avatars split
   into several skins need every one the garment overlaps; a chest-and-up mesh listed for
   something worn at the hips measures nothing, and the tool will say so.
2. **Centre from** seeds the mirror plane, normally the avatar root or its hips. Only the
   direction is taken from it; the exact position is fitted to the body mesh, because a rig can
   sit a few millimetres off the skin it drives. The panel reports how symmetric the body turned
   out to be — if that number is not small, nothing measured against it means anything.
3. Select the part that is crooked, set the falloff, and press **Fit to body**.

The panel then reports what it did and what is left. What is left over is the two sides genuinely
being different shapes — that is the design, and it should stay.

**Why the body and not the garment's own mirror?** Because the garment cannot be trusted to say
where its own centre is. On the piece this was built against, the garment was 22mm off at the
median and 43mm at worst, while the body under it was symmetric to 0.00mm.

**Rigid trim** is on by default and matters more than it sounds. Lace, buckles and charms are
separate shells laid on a garment, and a falloff that fades across a metal ring stretches it. With
this on, each shell moves as a whole piece instead.

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

### Proportional Edit(比例編輯)

開 **Tools → Lumi Mesh Tools → Proportional Edit**。

這是給「本身不是不對稱,只是穿歪了」的衣物用的——下襬、領口、袖口一邊高一邊低。運作方式就是
Blender 的 Proportional Editing:抓住網格的一部分去動,周圍會依距離衰減跟著動。

1. **選取** — *Loop* 模式點一下就抓整圈開放邊界(下襬、領口、袖口);*Island* 抓整塊連通的殼;
   *Brush* 用筆刷手動塗。
2. **設定衰減** — *Radius* 是衰減的距離。**Offset** 則讓滿強度先維持這麼一段再開始衰減,這正是你說的
   「影響範圍要超過一些」:如果從選取處直接開始衰減,選取的部分整個移動、旁邊卻幾乎不動,那個落差就會
   變成一道折痕。把 Offset 推到折彎會發生的位置之外,折痕就消失了。
3. **編輯** — 拖曳 handle,或按 *Level the selection* 自動擬合那圈的平面並轉正。*Apply* 確認、
   *Cancel* 取消、*Undo* 回退已確認的編輯。
4. **Bake & Apply** 存檔並指派,和 Symmetrize 一樣。

**Stitch pieces within(縫合距離)** 比看起來重要:蕾絲、吊飾、繫帶通常是「疊在」衣服上的獨立殼,
沿著表面量距離會直接繞過它們——衣服動了、裝飾留在原地飄著。距離另一塊在這個範圍內的就視為相連。
一般服裝設一兩公釐就夠。

### Fit to body(以身體為基準校正)

買來的衣服常常不是穿正的:腰帶一邊高一邊低、下襬歪掉。直覺的作法是「讓衣服左右對稱」,但那通常是錯的
——你想消掉的歪斜,和設計者刻意做的不對稱,混在同一份 mesh 裡。把一半鏡射到另一半,衣服是正了,設計
也一起沒了。

**Fit to body** 把這兩件事分開。它不取代任何幾何,只針對你選取的部分求出一個旋轉和位移,再透過一般的
falloff 套用出去。

1. 在 **Body reference** 指定角色的身體 mesh(皮膚,不是衣服)。身體拆成好幾塊的角色,要把衣服有
   重疊到的每一塊都列進去;拿「胸部以上」的 mesh 去量腰部的衣服,量到的是空的——工具會直接告訴你。
2. **Centre from** 用來決定對稱平面的方向,通常填角色根物件或 Hips。**只取方向**,精確位置是拿身體
   mesh 自己擬合出來的,因為骨架可能跟它驅動的皮膚差個幾公釐。面板會顯示身體本身的對稱度——如果那個
   數字不夠小,任何拿它當基準量出來的東西都不能信。
3. 選取歪掉的部分,設好 falloff,按 **Fit to body**。

面板會告訴你它轉了多少、移了多少,以及還剩多少。**剩下的就是兩邊形狀本來就不一樣**——那是設計,應該
留著。

**為什麼用身體而不用衣服自己的鏡像?** 因為衣服講不出自己的中心在哪。開發時用的那件,衣服自己偏了
22 mm(中位數,最大 43 mm),而它底下的身體對稱到 0.00 mm。

**Rigid trim(剛體飾件)** 預設開啟,比看起來重要:蕾絲、扣環、吊飾都是疊在衣服上的獨立殼,falloff
從中間淡出會把金屬環拉變形。開著的話,每個殼會整塊一起移動。

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
