# Sea Simulator — notes for Claude

AI-assisted Unity 6 (6000.6, URP) underwater simulation, built as a public portfolio piece.
The user sets the direction; Claude writes most of the code and drives the editor through the `unity-mcp` server.
Roadmap: fish schools (done) → seabed shaped by erosion → corals and relief → underwater look (murk, distortion, light).

## Workflow: ask first, let the user test

- An idea we discussed is not approval to implement it. Propose, wait for a clear yes, then do it.
- One branch per feature (`feature/...`), never directly on `main`. If a change falls outside the current branch's scope (e.g. a predator while on a boids branch), ask which branch to use before writing code.
- When work is ready, stop and let the user test it in Unity. Commit, push and merge only after they confirm, even if they asked for a merge earlier in the request.

## Keep the project clean

- While making a significant change, keep a running list of the files and scene objects it makes obsolete or unused (replaced scripts, shaders, materials, species, prefabs, models, textures, disabled scene objects, settings no longer read). Check with a search that nothing still references them.
- When handing the work over for testing, show that list to the user, so they test the version without those files in mind.
- Once the user has validated the change, remove the listed files (and their `.meta`) before committing and pushing, then confirm in the console that nothing broke (no missing scripts or references). Mention the removals in the commit message.
- Never remove anything the user added or tuned themselves without asking first, and keep anything they said to keep for now.

## Language and git

- Talk to the user in French. Write code, comments, shader code and **all commit messages in English**.
- No git identity is configured on this machine. Commit with
  `git -c user.name="nahugondev" -c user.email="45481067+nahugondev@users.noreply.github.com" commit ...`
  and end messages with the `Co-Authored-By: Claude` line (the project is openly AI-assisted).
- Ask before committing or pushing.
- `.gitattributes` routes binary assets through Git LFS: a tiny size in `git diff --stat` for an image is an LFS pointer, not data loss.

## README

- Keep the "AI Assisted" notice near the top and the user's own wording in it.
- When a roadmap step lands, tick it in the README and add a short, plain-language feature list.

## Code layout and conventions

- Simulation code lives in `Assets/Simulation/<Feature>/`, inside the `SeaSim.Simulation` assembly (`Assets/Simulation/SeaSim.Simulation.asmdef`), namespace `SeaSim.<Feature>`.
- Keep it data-oriented, like `Fish/`: `NativeArray` + C# Jobs + `[BurstCompile]`, no managed allocations in `Update`, dispose every native container in `OnDisable`.
- Expose tuning values in the Inspector, in real-world units or relative to body size, with tooltips.
- Prefer procedural content (meshes, terrain) over imported assets, and keep it explainable: it is a showcase.
- Custom URP shaders: every pass (`UniversalForward`, `ShadowCaster`, `DepthOnly`, `DepthNormals`) must apply the same vertex deformation, and per-material properties go in the `UnityPerMaterial` CBUFFER (SRP Batcher).
- Unity 6.6 API: `Object.GetInstanceID()` is obsolete (use `GetEntityId()`), and `FindFirstObjectByType` is deprecated (use `FindAnyObjectByType`).

## Working through Unity MCP

- After a script change, Unity recompiles and the MCP connection drops for a few seconds ("Unity not detected"): retry, then check the console for errors.
- Leave Play Mode before editing scripts, and remember that anything changed in Play Mode is discarded.
- `Unity_RunCommand` scripts cannot use `System.Reflection` or `System.Diagnostics`. Add a public read-only accessor to the component instead, and time with `Time.realtimeSinceStartup`.
- `Unity_Camera_Capture` can fail: render the camera into a `RenderTexture` and save a PNG to the scratchpad instead.
- When the editor is not focused, the player loop barely ticks. For measurements, step the simulation by hand (`SendMessage("Update")` / `("LateUpdate")`) and null the material while doing so, otherwise queued `RenderMeshInstanced` draws pile up into ghost trails.

## Validating changes

- Back each behaviour change with numbers measured before and after (for example: % of fish overlapping, nearest-neighbour distance, alignment, ms per step), plus a screenshot.
- End with a clean console (no errors or warnings).
