# Global Chat Commands (Everyone)

These slash commands can be used by all players in Global chat.

## Important rules

- Slash commands only work in Global chat.
- If you run a slash command in another channel, the server rejects it.

## Commands

### /help
Shows the command help summary.

For regular players, this shows the public command list.

Usage:
/help

### /bug {message}
Sends a bug report message to server logs.

Usage:
/bug {message}

Example:
/bug Mission gameplay is stuck. It is the AI turn but nothing is happening.

### /setaccountname {name}
Sets your account display name. Note that this will take effect after your next full login.

Usage:
/setaccountname {name}

Example:
/setaccountname NeonRunner

### /resetskills
Resets your active character skill tree and refunds all spent karma.

Usage:
/resetskills

Result:
- Purchased skills are cleared and reset to initial skills.
- Refunded karma is added back to current karma.
- Spent karma tracking is set to 0.
- For non-admins, a per-career cooldown of 28 days applies.
- On success, the server returns the next UTC timestamp when this command is available again.
- If used during cooldown, the server returns the next UTC timestamp when it can be used again.

## Notes

- Command names are case-insensitive.
- Some commands need required arguments. If arguments are missing, the server returns usage help.
