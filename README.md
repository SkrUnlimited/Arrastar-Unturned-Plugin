# Arrastar - Player Drag System for Unturned

Arrastar is a RocketMod plugin for Unturned that provides a custom and controlled player drag system using Harmony patches.

Designed for roleplay and moderated servers, it gives administrators full control over how dragging works on the server.

---

## ✨ Features

- Custom player drag behavior
- Permission-based control
- Configurable server messages
- Safe drag release handling
- Server-side validation
- Lightweight and optimized

---

## 🔐 Permission

arrastar.drag


Only players with this permission can use the drag feature.

---

## ⚙️ Configuration

All settings can be customized inside:

ArrastarConfiguration.cs


Example options:

- EnableVehicleDrag
- VehicleNeedsTwoSeatsMessage
- DraggedPlayerCannotExitVehicleMessage

You can adjust messages and behavior to fit your server style.

---

## 🛠 Installation

1. Build the project in **Release** mode
2. Place the compiled `.dll` inside:

/Rocket/Plugins/


3. Restart your server

---

## 🎮 Recommended For

- Roleplay servers
- Police / arrest systems
- Moderated PvP environments
- Custom gameplay servers

---

## 🏗 Built With

- C#
- RocketMod
- Harmony
- Unturned API

---

## 🧩 Contributing

Contributions, suggestions and improvements are welcome.  
Feel free to open an issue or submit a pull request.

---

## 📌 Future Improvements

- Additional drag conditions
- Extended vehicle validation
- Config file auto-generation
- More permission granularity

---

## 👨‍💻 Author

SkrUnlimited

---

## 📄 License

MIT License
