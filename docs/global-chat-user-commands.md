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

### /deleteaccount
Permanently deletes your account and all associated data. This is an irreversible operation that requires three-step confirmation with temporary codes.

**This command is not available to admin accounts.** If you are an admin, you must have your admin privileges removed before you can delete your account.

The deletion flow works as follows:

**Step 1** — Start the process:
```
/deleteaccount
```
The server sends you a 4-digit confirmation code.

**Step 2** — Confirm with the first code:
```
/deleteaccount {code}
```
The server sends a second 4-digit code and a final warning.

**Step 3** — Final confirmation with the second code:
```
/deleteaccount {code}
```
Your account data is permanently deleted and you are immediately disconnected.

Each code expires after 5 minutes. Only one active code exists at a time — running `/deleteaccount` at any point restarts the process with a new code.

## Notes

- Command names are case-insensitive.
- Some commands need required arguments. If arguments are missing, the server returns usage help.
