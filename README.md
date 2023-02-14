# VRC-USharp-Scripts
Packer's VR Chat Udon Sharp scripts, for all to use in thier own worlds.

### How to install Scripts
**Installation:**
Right click in the `Project` tab inside of unity, `Import Package > Custom Package...` and select the desired package. Click Import on the bottom right `Import Unity Package` window.

**Use**
On the desired gameObject, Add component and select the desired script. Scripts will automatically add any additional missing componenets automatically, they will also be set to a common use case requiring no additional setup if desired.

### VRC Advanced Pickup
A simple companion script to VRChat's VRCPickup component, it handles a low network traffic cost synchronization of pickups. Rather than continuous synchronizing of a pick up (high bandwidth use), it sends basic position and rotation infomation when picked up to all the clients and handles the rest locally freeing up world bandwidth.

Download: https://github.com/Packer/VRC-USharp-Scripts/releases

|**VRC Advanced Pickup (New)**|**VRC Object Sync (Old)**|
| ------------- | ------------- |
|![AdvPickup](https://user-images.githubusercontent.com/4197534/218288273-ffa8c7f0-35e8-4bcb-a863-70d0cb5adb8e.gif)|![OldPickup](https://user-images.githubusercontent.com/4197534/218288282-1e7c39e3-2a27-4123-8342-e7251d1083a9.gif)|

### Limitations
When a player uses a pickup, there is a chance the pickup will be off center from the hand. This occurs when a player is in desktop mode and moves erratically immediately after pickup. This is the tradeoff for network free syncing, it cannot be helped.
Dropped rigidbodies only properly update if it's moved on the owner's game, this is advanced behaviour outside the scope of this script.