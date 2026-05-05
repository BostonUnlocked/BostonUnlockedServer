# Global Chat Commands (Admin)

These slash commands are only available to chat admins in Global chat.

## Important rules

- Slash commands only work in Global chat.
- Admin access is checked against the chat admin account allowlist.
- If a non-admin runs an admin command, the server denies it.

## Admin-only commands

### /announce {message}
Broadcasts a server announcement.

Usage:
/announce {message}

### /activemissions
Shows how many missions are currently active.

Usage:
/activemissions

### /totalaccounts
Shows total account count and authentication split.

Usage:
/totalaccounts

Output:
- Total accounts
- Steam-authenticated accounts
- Non-Steam accounts

### /onlineplayers
Shows the number of currently logged-in players.

Usage:
/onlineplayers

### /listplayers
Lists connected players with category tags.

Usage:
/listplayers

### /setavailablemission {MissionId} [Available]
Sets the main-campaign mission state for your active career.

State behavior:
- If state is omitted, the mission is set to ReadyToPlay.
- If state is Available, the mission is set to Available.

Usage:
/setavailablemission {MissionId}
/setavailablemission {MissionId} Available

### /getkarma
Reads your active character karma.

Usage:
/getkarma

### /getnuyen
Reads your active character nuyen.

Usage:
/getnuyen

### /setkarma {X}
Sets your active character karma.

Usage:
/setkarma {X}

### /setnuyen {X}
Sets your active character nuyen.

Usage:
/setnuyen {X}

### /setcareerslots {N}
Sets how many career slots are available for your account.

Usage:
/setcareerslots {N}

### /additem {ItemCode} [Variant]
Adds one item to your active character inventory.

Usage:
/additem {ItemCode}
/additem {ItemCode} {Variant}

Variant rules:
- Variant must be an integer from 2 to 405.
- Variant compatibility is validated against static data.

### /othergetkarma {AccountId}
Reads karma for another connected account.

Usage:
/othergetkarma {AccountId}

### /othergetnuyen {AccountId}
Reads nuyen for another connected account.

Usage:
/othergetnuyen {AccountId}

### /othersetkarma {AccountId} {X}
Sets karma for another connected account.

Usage:
/othersetkarma {AccountId} {X}

### /othersetnuyen {AccountId} {X}
Sets nuyen for another connected account.

Usage:
/othersetnuyen {AccountId} {X}

### /othersetcareerslots {AccountId} {N}
Sets how many career slots are available for another connected account.

Usage:
/othersetcareerslots {AccountId} {N}

### /otherresetskills {AccountId}
Resets another connected account's active character skill tree and refunds all spent karma.

Usage:
/otherresetskills {AccountId}

Result:
- Purchased skills are cleared and reset to initial skills.
- Refunded karma is added back to current karma.
- Spent karma tracking is set to 0.

### /otheradditem {AccountId} {ItemCode} [Variant]
Adds one item to another connected account's active character inventory.

Usage:
/otheradditem {AccountId} {ItemCode}
/otheradditem {AccountId} {ItemCode} {Variant}

## Notes

- Command names are case-insensitive.
- Other-account commands require the target account to be currently online.
- Values for karma and nuyen must be non-negative integers.
- Career slot counts must be integers from 1 to 8.
- Item and variant validation use server static data.
