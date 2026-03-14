# Changelog

## \[1.1.1]

* Fixed vent/puzzle progress getting wiped when the host dies and becomes a ghost
* Fixed the client not being able to use the exit door when the host is a ghost
* Fixed client's vent map showing all vents as red/off until a puzzle was completed
* Fixed clown killing the client while they were looking at it (host's view was overriding client's)
* Fixed ghost players still getting chased by Smile, Jeff, and Sparky
* Fixed dead player's battery blocking their partner from using that vent
* Fixed host's battery not working at the exit door when the client already placed theirs
* Fixed painting entity killing both players when only one should die
* All alive players must be in the main room to use the exit door
* Disabled voice chat (too experimental — will return in a future update)

## \[1.1.0]

Some of these features are still early and may not work perfectly in every situation. If you run into issues, report them on the [Discord](https://discord.com/invite/BDZ6NjegeR).

### Death & Ghosts
- You now get a proper jumpscare when you die, even if both players die at the same time
- When you die, you become a ghost and can spectate your partner
- Ghosts can't interact with anything — no puzzles, batteries, or cranks
- Ghost minimap stays open so you can watch your partner's progress
- Fixed a bug where dying would sometimes skip the jumpscare entirely

### Monsters
- Henry and Harold now move the same for both players (host controls them)
- Sparky, Jeff, and Smile are independent — each player deals with their own
- Paintings can now kill each player separately (no more both dying because one person looked at a painting too long)
- Fixed Henry getting stuck in the main room
- Fixed Harold freezing after killing someone
- Monsters now chase the closest alive player instead of only targeting the host

### Puzzles
- Puzzle progress syncs between players
- Both players can work on different puzzles at the same time
- Fixed the client seeing all vents as broken on the minimap when loading in
- Added a lock system so two players can't grab the same puzzle block at once

### Battery & Charging
- Each player has their own battery
- You can see your partner's battery when they place it in a crank or puzzle station
- Crank charge display is now smooth instead of stuttery
- Fixed a bug where the host couldn't place their battery at the exit door if the client already did

### Visuals & Audio
- Remote players now have a helmet model
- You can see your partner's hand grips and trigger pulls
- Cyan friend indicator on the minimap
- You can hear your partner crawling through vents

### Sync
- Paintings, clown, vent doors, flashlights, and the exit door all sync between players
- Scene transitions sync with a fade effect
- Night selection is controlled by the host

### Networking
- Ping display
- Players on different mod versions get a warning
- Host migration if the host disconnects
- Fixed death notifications not sending when VR headset was taken off mid-game

### Other
- Streamer mode — press Insert to hide the UI
- Fixed mod files deploying to the wrong folder on build

## [1.0.0]

- Initial release
