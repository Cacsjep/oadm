---
title: Device groups
description: Limit a user to part of the devices, by site, tag or single device.
---

A device group limits a user to part of the devices. The role still decides what the user may do; the group decides on
which devices. One Operator role and a group "Building B" replaces a role per site.

## What a group holds

| Rule | Devices in the group |
|---|---|
| **Sites** | Every device of the chosen sites. **This server** is every device without a site |
| **Tags** | Every device with one of the chosen tags |
| **Single devices** | Devices you tick one by one |
| **Except** | Devices left out, even when a site or tag covers them |

Sites and tags keep up: a new device at a chosen site or with a chosen tag is in the group at once. Single devices are
kept by serial number, so a removed device that is added again is in the group again. A user with all devices or a user
of that group must add it.

## Create a group

**Access** page > card **Device groups** (administrators) > **New device group**.

1. Enter a name and, optional, a description.
2. On the left, tick sites and tags. Their devices show ticked on the right, with the site or tag that covers them.
3. On the right, tick single devices. **Except** leaves a covered device out; **Include** takes it back.
4. The count shows how many devices the group has ("412 of 4,812 devices"). **Save**.

The card lists each group with its rules ("Site Building B, tag PTZ, 3 devices, except 1"), its device count and its
users. The edit button of a row opens the same editor.

## Give a user a group

**New user**, or **Edit user** in a user row: the **Devices** field is **All devices** or a group. The change applies at
once; the user's client reloads the devices.

Administrators always have all devices; the **Devices** field says so for them.

The user sees the group and its rules under **Your role** (click the name in the title bar).

## What a user with a group sees and does

- **Devices page**: only the devices of the group. Tasks, live preview, Web View and the context menu work on these
  devices only. Live video and the web interface of a device close when the device leaves the group.
- **Tasks**: only the tasks of the group's devices. A queued task whose device left the group fails before it starts.
- **Add devices**: only at the sites of the group, so new devices land inside it. Discovery and the network range look
  only there; **This server** in the group allows the server's own network. A group of tags or single devices only
  cannot add devices: **Add** is off. The credential list is never used for these users: they enter the password
  themselves.
- **Tags**: they tag devices of their group, but never add or remove a tag that a group uses. Renaming and deleting
  tags stays with users with all devices.
- **Sites page**: only the sites of the group, with the group's counts.
- **Compliance**: the checks of their group, run only on its devices.
- **Plugin pages**: only their own devices, reports and live updates. Device triggers, Live View views and dashboards
  belong to the group of the user who made them; others do not see them. Image Health Dashboard, Edge storage and the
  Action Rule Editor show each group its own table.
- **Server-wide services**: the NTP server, DHCP server, PKI and EST server pages and the VAPIX Commander library are
  read only for them, with the rows of their own devices. They run saved VAPIX commands only with the passwords they
  enter.
- **Not for them**: event rules and the event log (the **Event rules** page is hidden), the server log, the system
  report, backups, the audit log and server settings. Plugins not yet built for device groups are hidden for them too.

## Delete a group

The red **Delete** in a group row names the users who lose their devices. After the delete these users have **No
devices** until you give them another group or **All devices**. The compliance checks of the group are turned off;
device triggers, views and dashboards of the group stop.

Deleting a tag or removing a site that a group uses asks first: "Used by device group Building B". The tag or site then
drops out of the group.

## After the update

Every existing user keeps all devices, also after restoring a backup of an older OADM. Nothing changes until you create
a group and give it to a user.

Every change of a group, of a user's group and every device that enters or leaves a group is in the audit log.
