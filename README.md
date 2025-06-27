# 🐾 CatSurvivalCraft – NoSlimes Survival Prototype

A **first-person multiplayer survival crafting game** focused on scenic exploration, cooperative gameplay, creative construction, and tameable animal companions—especially cats.

This repository contains select systems and prototypes developed for the game as part of an ongoing portfolio project.

> ⚠️ **Disclaimer**  
> This codebase is **under active development** and is intended for **portfolio and educational purposes only**. It is **not the full game** or a finished product.

---

## Current Features

### ✅ First-Person Player Controller (`FirstPersonController.cs`)
- Built for **Unity Netcode for GameObjects (NGO)**
- **Server-authoritative movement** with **client-side prediction** (host excluded for accuracy)
- Player input is serialized via a `PlayerMovementInputData` struct and sent to the server each frame using RPCs
- The server processes all movement logic and updates animation states
- Camera pitch is synchronized with a `NetworkVariable<float>`
- Non-owners are prevented from controlling the `CharacterController` or camera directly
- Supports:
  - Sprinting, jumping, gravity, fall detection
  - Ground check using physics sphere
  - Smooth acceleration & deceleration
  - Mouse and gamepad look input
  - Vertical camera pitch clamped via **Cinemachine**
- Animation parameters (`Speed`, `Walking`) updated based on state

### ✅ Inventory System (`Inventory.cs`, `PlayerInventory.cs`)
- Modular, container-based design
- Server-authoritative item handling
- **Drag-and-drop inventory UI implemented and functional**

### ✅ Player Hotbar & Item Interaction (`PlayerHotbar.cs`)
- Server-authoritative hotbar system synchronized via `NetworkVariable<int>` for selected slot index
- Supports cycling through hotbar slots via scroll input and direct slot selection
- Uses Unity Input System callbacks registered only for the owning player
- Selected tool items can be *used* through input (e.g., attack action) with server RPC validation
- Item usage triggers gameplay effects on server and replicates visuals/effects to clients via client RPC
- Spawns item visuals in the player’s hand socket on all clients to represent the currently equipped tool
- Item usage logic is delegated to `ItemUsageSO` subclasses such as `ResourceToolUsageSO` (handles raycasting and resource node damage)
- Robust error and state checking with detailed debug logging using `DLog`

#### Networking Details
- Client inputs trigger server RPCs (`UseItemServerRpc`, `SetSelectedIndexServerRpc`) to ensure server authority
- The server validates and applies item usage effects before broadcasting visual feedback to all clients
- Item instance data is tracked and used to ensure proper contextual behavior (e.g., durability, unique IDs)
- Replicated visuals (spawned prefabs for equipped items) are controlled on clients to maintain sync and immersion

---

## Dependencies

- [Unity Netcode for GameObjects (NGO)](https://docs-multiplayer.unity3d.com/)
- [Unity Input System](https://docs.unity3d.com/Packages/com.unity.inputsystem)
- [Cinemachine](https://docs.unity3d.com/Packages/com.unity.cinemachine)
- **Custom Systems:**
  - `EntityStats` – Movement speed and stats system
  - `InputManager` – Centralized input handling
  - `Character` – Marker or logic wrapper
  - `DLog` – Logging utility, publicly available on [GitHub](https://github.com/NoSlimes/DLog)

---

## Planned Features

- Modular **building system** with snap points (Valheim-inspired)
- **Tameable animal companions** with unique behaviors (starting with cats!)
- **Resource gathering** and crafting
- **Dynamic weather** and **day-night cycle** using UniStorm

---

## Author

Made with lots of 🐈 by **NoSlimesJustCats**  
🔗 [github.com/NoSlimes](https://github.com/NoSlimes)

---

## License

This repository is for **portfolio and educational demonstration purposes only**.  
**Do not reuse, distribute, or commercialize** any code or assets without explicit permission from the author.
