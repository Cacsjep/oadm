# Event Schedules (Version 2.0.1)

## Overview

- **Short Description**: Manage scheduled events
- **ID**: event-schedules
- **Version**: 2.0.1
- **State**: Released

This API is based on the **Device Configuration API** framework. For guidance on how to use these APIs, please refer to the `Device Configuration APIs` section in the VAPIX Library.


<!---{Extra-Doc:api-description}--->

<!---{Extra-Doc:use-cases}--->

## API Definition

### Structure

```text
event-schedules.v2 (Root Entity)
    ├── schedules (Entity Collection)
        ├── id (Property)
        ├── name (Property)
        ├── schedule (Property)
        ├── scheduleType (Property)
```

### Entities

#### event-schedules.v2 {#event-schedules.v2}

- **Description**: Manage scheduled events
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:event-schedules.v2}--->

##### Properties

This entity has no properties.

##### Actions

This entity has no actions.

---

#### event-schedules.v2.schedules {#event-schedules.v2.schedules}

- **Description**: Schedules
- **Type**: Collection (Key Property: [id](#event-schedules.v2.schedules.id))
- **Operations**
    - **Get**
    - **Set**
        - **Properties**: name, schedule
    - **Add** (**Permissions**: admin)
        - **Required properties**: name, schedule
        - **Optional properties**: id
    - **Remove** (**Permissions**: admin)
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:event-schedules.v2.schedules}--->

##### Properties

###### id {#event-schedules.v2.schedules.id}

- **Description**: Unique identifier for a schedule. Automatically generated if not provided during add.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:event-schedules.v2.schedules.id}--->

###### name {#event-schedules.v2.schedules.name}

- **Description**: Well-known name of the schedule.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:event-schedules.v2.schedules.name}--->

###### schedule {#event-schedules.v2.schedules.schedule}

- **Description**: Schedule definition, based on the iCalendar format.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:event-schedules.v2.schedules.schedule}--->

###### scheduleType {#event-schedules.v2.schedules.scheduleType}

- **Description**: Type of schedule, either 'Pulse' or 'Interval'.
- **Datatype**: [ScheduleVariant](#datatypes.ScheduleVariant)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:event-schedules.v2.schedules.scheduleType}--->

##### Actions

This entity has no actions.

---

### Data Types

#### ScheduleVariant {#datatypes.ScheduleVariant}

- **Description**: Type of schedule
- **Type**: string
- **Enum Values**: "PULSE", "INTERVAL"

<!---{Extra-Doc:datatypes.ScheduleVariant}--->

