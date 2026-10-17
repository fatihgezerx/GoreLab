# GoreLab

GoreLab takes parts off a skinned character: a head, an arm, a leg. The cut closes with a stump on the body and a lid on the part, and the part flies off on a body of its own, as it was posed at the moment of the cut. It also breaks a static object (a table, a crate, a statue, a wall) into pieces.

## Overview

A character's skinned meshes are split once, in the editor, into one part for every place it can be cut. At run time a cut only switches things: the part is hidden on the body, the stump shows, and a snapshot of the part's pose becomes a physical object that is thrown away. Nothing extra is drawn or simulated until the first cut, and nothing runs every frame.

It works on humanoid rigs (the head, both arms in three pieces, both legs in three pieces) and on any other rig whose cuts you name yourself, and on characters made of several skinned meshes (body, clothes, hair), which are cut along the same bones.

## Features

- **Gore Studio** (`Tools > Gore Studio`): drop a character prefab in (nothing is put in a scene) and see it in a preview you orbit like in Blender (middle mouse orbit, Shift + middle pan, wheel zoom, numpad views, a gizmo with a perspective / orthographic switch). The panel has the Prefab, its Mesh type (skinned or static) and, for a skinned character, its Rig (humanoid or generic): all detected, all changeable; then the skinned meshes (Cut, Lids, Show) and the cuts (a humanoid gets its thirteen by itself; for another rig pick bones from a menu). The preview paints every part of every cut in its own colour and draws the cut rings and the lids (the second toolbar group, or M, switches to the character as it is, with its materials). The **Worn equipment** card scans what the character wears and **Dress** puts it on the naked character; the **Cloth parts** card then sets each piece up: **Cut** (a tick box; needs Read/Write): the piece is cut along the body parts, and the piece on the body side and the piece on the part side each follow or fly. Not cut (the default): the piece goes whole with the part it hangs on (Auto, or any part you choose), following it or flying off; when the mesh holds separate pieces that hang on different parts (the two trouser legs) the card offers to **Divide** it so each piece gets its own part. Its toolbar (top left) has click select and paint select (C: drag to paint, wheel = brush size) and vertex / edge / face (1, 2, 3); the bar at its bottom adds the selection to a cut (*Add to cut* takes everything between the selection and the cut's part, so a ring or a strip is the line to go up to) or removes it. Apply sets the prefab up (the parts are saved next to the profile).
- **Parts and lids made from the mesh itself:** the skin follows the bone weights, seams are closed with a stump on the body and a lid on the part (both deform with the skin).
- **A cut takes everything below it:** cutting an upper arm takes the forearm and the hand with it; cutting a forearm first leaves the upper arm to be cut later.
- **Several skinned meshes** per character, **any rig** (humanoid shortcuts, or your own named cuts).
- **Profiles are shared:** one profile for every character with the same meshes and bones; the meshes are assets next to it and a new setup updates them in place.
- **Static objects (`GoreBreakable`):** the Gore Studio's static mode divides a mesh with no bones into pieces, by hand (Manual) or at random (Random: a slider for the number of pieces, a live coloured preview, a seed and Reroll), after optionally adding triangles (*Extra triangles* + *Preview*, everywhere or only on the faces you selected) so even a flat wall of two triangles can be divided finely. Apply saves a *Gore Break Profile* with one mesh per piece and puts a `GoreBreakable` on the object. At run time `Break(point, force, radius)` breaks off the pieces near a point (radius 0 or infinity = all), `Sever(piece, impulse)` takes one piece, `IsPieceBroken`, `ResetBreakable()` (for a pooled object), events `Severed` and `Restored`. The broken faces of a closed mesh get a lid with a fill material (wood for a table); an open mesh (a wall) breaks into thin shards.
- **Code:** `GoreCharacter.Sever(GoreLimb | name | number, impulse)`, `IsSevered`, `ResetCharacter` (for a pooled character), events `Severed` and `Restored`.
- **No `Update`:** a part removes itself after a time through one UniTask delay and frees its meshes.
- Standalone: its own assemblies and a `DependencyGuard` that offers to install UniTask; other systems (the Combat System's finisher, later) use it through the `HAS_GORE_LAB` symbol.

## Setup

### Requirements

- Unity 2022.3 LTS or newer (made with Unity 6)
- [UniTask](https://github.com/Cysharp/UniTask): the setup guard offers to install it
- A skinned character: meshes with bone weights (no Read/Write needed on their models)

### Installation

Copy the repository into your project's `Assets/` (e.g. `Assets/Scripts/GoreLab`). `Editor/Setup/DependencyGuard.cs` runs once the scripts load: it sets the `HAS_UNITASK` and `HAS_GORE_LAB` scripting symbols and, if UniTask is missing, offers to install it. Until it is installed GoreLab is left out of compilation, so the project keeps compiling.

## Quick Start

1. Open **Tools > Gore Studio** and drop the character's prefab into the Target field (no need to put it in a scene).
2. The skinned meshes are listed (the biggest has **Lids** on: the skin; clothes usually stay open). A humanoid gets its thirteen cuts by itself; for another rig press **Add cut** and pick the bones from the menu. The preview shows where each cut will separate the meshes.
3. Press **Apply to prefab**. A **Gore Character** component is added to the prefab, a *Gore Profile* and its meshes are saved under `Data/`, and the switched-off parts appear under a `GoreParts` object next to each mesh.
4. Put the prefab in a scene.
5. Press Play, select the character, and press **Sever** next to a cut in the Gore Character's Inspector. **Reset character** puts everything back.

From code:

```csharp
var gore = character.GetComponent<GoreCharacter>();
gore.Sever(GoreLimb.LeftLowerArm, hitDirection * 4f);   // the part flies off with an impulse
if (gore.IsSevered(GoreLimb.Head)) { }
gore.ResetCharacter();                                   // a pooled character used again
gore.Severed += (cut, part) => { /* blood, a sound... */ };
```

## How it works

- **Segments and the cut line.** The body is segment 0 and every cut is another segment: the skin that follows that cut's bone and nothing below another cut. For each cut every vertex has a share: how much of its bone weight sits on the cut's bone and everything below it. The cut is the line where that share is one half (the middle of the joint's blend). The triangles that cross the line are clipped along it, with new vertices on their edges (position, weights, normals, tangents, UVs and colours blended), so the line is a clean smooth ring and not the staircase that deciding triangle by triangle would leave. A triangle belongs to the deepest cut it lies inside.
- **Seams and lids.** Where a segment meets the one it is cut from, the triangles of the two end at a seam. The seam's edges (vertices at the same place are welded, so hard edges and texture seams do not matter) are chained into closed loops, and each loop gets a fan of triangles round a centre point: a lid on the part and a stump on the body, facing opposite ways and following the same bones as the skin they close.
- **Run time.** Until the first cut the character is drawn by its own renderers. Then they are switched off and the parts take over (together they look the same; a mesh that was off or inactive stays hidden, and materials that were changed meanwhile are carried over). A cut switches the part off on the body, shows the stump, and bakes the pose of the part (and its lid, and the stumps that were on it) into plain meshes on a new object with a box collider, a rigidbody and a throw. The skin that stays on the body stops following the bones of the part: its renderers are given helper transforms under the bone above the cut, at the offset the bones had in the bind pose (kept in the profile), so the blended skin round the stump moves exactly like the stump's lid and the hidden limb, which keeps animating, cannot drag it out of line. The bone of the cut is also squeezed to almost nothing until `ResetCharacter`, so a hit collider or a weapon hanging on that bone goes with it.
- **Where two parts touch.** A cut's seam counts every neighbour that does not come off with it, so the hip of a leg is closed even where it touches the other leg. A seam that cannot be closed (an open border of the mesh) or that touches itself is reported by the setup.

- **Static objects: how a mesh is broken.** The pieces are patches of the mesh's surface. *Extra triangles* splits the biggest triangle again and again along its longest edge (and the triangle on the other side along the same edge, so no crack opens; a hard edge or a texture seam stays a seam). *Random* spreads seeds over the surface (the next seed is likely to be far from the earlier ones; every separate piece of the mesh gets one) and gives each triangle to the seed it can be reached from at the least cost along the surface, so every piece is one connected patch; the same seed gives the same pieces. *Manual* pieces are drawn with the studio's selection tools; the triangles given to no piece make one *rest* piece. On a closed (watertight) mesh the edge where two pieces meet gets a lid: a cone from the broken edge to a point inside the solid, half way through it under the patch, so a piece is a chunk with a rough broken face and not a sheet as thin as paper (it uses the fill material, mapped by the side it faces so the grain matches the size of the object). A piece weighs the volume of the whole mesh times the share of the surface it has times the density; an open mesh uses its surface times *Open Thickness*. At the first break the pieces are made under the object (one renderer each) and its own renderer is switched off; a piece that breaks off gets a box collider, a rigidbody and the throw (away from the point, weaker with distance). Pieces do not collide with the object they came from or with each other.

## In the editor

With the optional **Veneer** system in the project, GoreLab gets the shared studio editor look in the brand colour (deep magenta-red). Without the kit nothing is lost: the Inspectors fall back to the plain IMGUI ones and the Gore Studio keeps its IMGUI window (the setup guard sets the `HAS_VENEER` symbol while the kit is installed; the studio-look code is in the separate `GoreLab.Editor.Studio` assembly, so the rest of the editor code compiles without it).

- **Gore Character Inspector.** A header with a state pill (*Not set up*, *Ready*, and in Play Mode *Parts off*), the profile slot with an *Open Gore Studio* button, hints while the character is not set up, and the list of cuts. In Play Mode every cut shows *On* / *Severed*, a *Sever* button for the ones still on, and a *Reset character* button.
- **Gore Profile Inspector.** Three tabs: *Settings* (the cut-off parts card: despawn time, density, spin, cut worn equipment; the blood card: prefab, *Use the default blood*, bleed time, scale), *Cuts* (read only: every cut with its parent and the cuts below it) and *Meshes* (read only: one card per skinned mesh with its source, lids, parts, triangles).
- **Layout-driven Inspectors.** Both Inspectors are drawn from a default layout that GoreLab ships (`GoreCharacterDefaultLayout`, `GoreProfileDefaultLayout`), through the Veneer layout engine. Open `Tools > Veneer > Inspector Designer` to see their structure; *Customize* makes your own copy to rearrange (tabs, cards, fields, notes), *Reset* returns to the default. The pieces that are not fields (the state pills, the parts-off strip, the Open Gore Studio and Use the default blood buttons, the cuts list, the two read-only summaries) can be moved or hidden in the Designer but not changed. The raw data arrays of a profile (cuts, mesh sets, sections, cloth rules) are hidden by default; the Designer can show them.
- **Gore Studio window.** The window has a UI Toolkit shell: a title bar with the logo and a state pill (*Nothing selected* / *Target selected* / *Baking* / *Problem*), a two-pane layout (panel at the left, 3D preview at the right), the *Target*, *Fill material* and *Apply* cards as kit cards, and tabs *Meshes*, *Parts*, *Cloth* (skinned target) or *Static* (static target) for the panels. The tabs' panels and the preview with its tools (object / edit mode, selection tools, X-ray, hotkeys) are still the IMGUI code, hosted inside the shell, and behave as before.

## Limits (the first version)

- **Static objects:** at most 100 pieces (each is a physical object of its own); a piece that breaks off has a box collider fitted to its bounds, not its shape; the object's own collider stays until every piece is gone; one `GoreBreakable` per mesh object (a prefab with several static meshes is set up mesh by mesh in the studio); an object in static batching cannot be taken apart at run time (leave it out of batching); cracks, dents and blood on the pieces are not done.

- Cuts are only at the prepared bones; a part flies off as one rigid piece (no limp limb, no ragdoll yet).
- **Showing the look in the studio (`IGoreLookPreview`):** the studio only ties dropped clothes to the skeleton. A data asset that also implements `GoreLab.IGoreLookPreview` (inside `#if HAS_GORE_LAB`) is asked after *Dress* to finish the picture: put skins on the naked body, change the clothes' materials, add the body renderers it hides to the list it is given. Its changes to the naked meshes' materials are undone by *Undress*.
- **Marking what is worn (`[GoreWorn]`):** put `[GoreLab.GoreWorn]` on the fields of your own data that name the equipment a character is dressed with at run time (a ScriptableObject with a list of items, say; inside `#if HAS_GORE_LAB` if your code must compile without this system), then drop that asset (or a prefab, or a folder of prefabs) into the **Worn equipment** card of the Gore Studio: it finds the skinned meshes, lists the ones without Read/Write, switches it on for their models and warns before Apply while some are still not readable.
- **Cloth rules:** a piece set up in the Cloth parts card keeps its rule in the profile (`ClothRules`, by mesh). A piece that is not cut never reads its mesh, so it needs no Read/Write; *Cut* needs it (an unreadable one warns and goes whole). A piece set to fly leaves as a body of its own. The cut ends of clothing are not filled yet.
- **Equipment (summary):** a character that is dressed at run time (armour, hair, boots put on by a look system) is cut as it is worn: every enabled skinned mesh the editor did not prepare is split along the same cuts in the background the first time the character starts (and again after `RefreshEquipment()`), cached per mesh. **Those meshes need Read/Write** (Model tab of the Import Settings); one without it stays on the body and the Console says which. The lids are widened to the armour.
- **Lids:** every mesh that has *Lids* on gets a lid on the part and a stump on the body that follow its own cut line exactly: what the Gore Studio preview shows is what the game fills. What a character wears is never filled in.
- **Blood** (phase G2): every cut sheds blood from the stump on the body and from the end of the part that comes off, from the profile's *Blood Prefab* (a Particle System; a new profile gets the default one, which spurts in weakening pulses and then runs; *Bleed Time* and *Blood Scale* in the profile, an older profile has a *Use the default blood* button). The blood stops after the bleed time and the effect is given back; with Pool System in the project (the `Pooling` folder, `HAS_POOL_SYSTEM`) the prefab comes from a pool when it is registered there. Decals, sounds and impact effects are the Surface Engine's job, not this system's. Wounds on the skin are still to come. The Combat System bridge (a finisher's *Simulate Gore*) is done and lives in the Combat System's optional `Gore` folder: it cuts a target's `GoreCharacter` at the finisher's cut marks and resets it when the fighter is revived; this system knows nothing about it.
- **No Read/Write needed:** Unity lets the editor read any mesh outside Play mode, so the characters' import settings stay as they are (set up in edit mode, not in Play mode). Only if Unity refuses a mesh anyway the setup says so and tells you to tick Read/Write for the setup and untick it afterwards: the parts are saved as assets and the original does not need it at run time. A skinned mesh without bone weights is refused with a message; blend shapes, UV channels 4 and up and non-triangle submeshes are not kept on the parts (the setup warns); vertices with more than four bones keep the four strongest.
- A very low-poly mesh may give a rough seam: check each new character.

## License

MIT, see `LICENSE`.
