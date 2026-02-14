# 🚗 Arrastar – Player Drag System

Advanced and fully configurable drag system for **Unturned** built with RocketMod + Harmony. Designed for immersive Roleplay and moderated servers.

## ✨ Features
- Custom drag distance and strength control  
- Smooth follow system with adjustable update interval  
- Teleport correction system (anti-desync)  
- Optional surrender requirement  
- Optional vehicle drag support  
- Permission-based access (`arrastar.drag`)  
- Fully customizable server messages  
- Optional debug logging  

## ⚙️ Configuration Highlights

Key adjustable options:

- `DragDistance` (default: 3)
- `DragStrength` (default: 75)
- `FollowDistance`
- `FollowIntervalSeconds`
- `FollowTeleportThreshold`
- `RequireTargetSurrendered`
- `EnableVehicleDrag`
- `EnableDebugLogging`

All messages and behavior can be customized inside `ArrastarConfiguration.xml`.

## 🛠 Installation
1. Build in **Release** mode  
2. Place the `.dll` inside `/Rocket/Plugins/`  
3. Restart the server  

## 🎮 Ideal For
- Roleplay servers  
- Police / arrest systems  
- Moderated PvP environments  

## 👨‍💻 Author
SkrUnlimited  

## 📄 License
MIT