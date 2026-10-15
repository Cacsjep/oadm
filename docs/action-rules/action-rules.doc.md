# Action Rules API (Version 2.0.1)

## Overview

- **Short Description**: API for configuring action rules
- **ID**: action-rules
- **Version**: 2.0.1
- **State**: Released

This API is based on the **Device Configuration API** framework. For guidance on how to use these APIs, please refer to the `Device Configuration APIs` section in the VAPIX Library.


<!---{Extra-Doc:api-description}--->

<!---{Extra-Doc:use-cases}--->

## API Definition

### Structure

```text
action-rules.v2 (Root Entity)
    ├── recipients (Entity Collection)
        ├── id (Property)
        ├── name (Property)
        ├── parameters (Property)
        ├── template (Property)
    ├── rules (Entity Collection)
        ├── actionConfig (Property)
        ├── activationConfig (Property)
        ├── enabled (Property)
        ├── id (Property)
        ├── name (Property)
    ├── templates (Entity)
        ├── actions (Entity Collection)
            ├── actionType (Property)
            ├── id (Property)
            ├── parameters (Property)
            ├── recipientTemplate (Property)
        ├── recipients (Entity Collection)
            ├── id (Property)
            ├── parameters (Property)
```

### Entities

#### action-rules.v2 {#action-rules.v2}

- **Description**: Action Rules API (root object).
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:action-rules.v2}--->

##### Properties

This entity has no properties.

##### Actions

This entity has no actions.

---

#### action-rules.v2.recipients {#action-rules.v2.recipients}

- **Description**: The recipient catalog configurations.
- **Type**: Collection (Key Property: [id](#action-rules.v2.recipients.id))
- **Operations**
    - **Get**
    - **Set**
        - **Properties**: name, parameters, template
    - **Add** (**Permissions**: admin)
        - **Required properties**: name, template, parameters
        - **Optional properties**: 
    - **Remove** (**Permissions**: admin)
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:action-rules.v2.recipients}--->

##### Properties

###### id {#action-rules.v2.recipients.id}

- **Description**: The recipient entry generated id.
- **Datatype**: [Id](#datatypes.Id)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.recipients.id}--->

###### name {#action-rules.v2.recipients.name}

- **Description**: The recipient entry name.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.recipients.name}--->

###### parameters {#action-rules.v2.recipients.parameters}

- **Description**: The recipient entry parameters.
- **Datatype**: [ParameterKeyValueArray](#datatypes.ParameterKeyValueArray)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.recipients.parameters}--->

###### template {#action-rules.v2.recipients.template}

- **Description**: The recipient template token.
- **Datatype**: [TemplateId](#datatypes.TemplateId)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.recipients.template}--->

##### Actions

This entity has no actions.

---

#### action-rules.v2.rules {#action-rules.v2.rules}

- **Description**: The rule configurations
- **Type**: Collection (Key Property: [id](#action-rules.v2.rules.id))
- **Operations**
    - **Get**
    - **Set**
        - **Properties**: actionConfig, activationConfig, enabled, name
    - **Add** (**Permissions**: admin)
        - **Required properties**: name, enabled, activationConfig, actionConfig
        - **Optional properties**: 
    - **Remove** (**Permissions**: admin)
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:action-rules.v2.rules}--->

##### Properties

###### actionConfig {#action-rules.v2.rules.actionConfig}

- **Description**: The configuration for the action
- **Datatype**: [ActionConfig](#datatypes.ActionConfig)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.rules.actionConfig}--->

###### activationConfig {#action-rules.v2.rules.activationConfig}

- **Description**: Configuration that determines when the action will be triggered.
- **Datatype**: [ActivationConfig](#datatypes.ActivationConfig)
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.rules.activationConfig}--->

###### enabled {#action-rules.v2.rules.enabled}

- **Description**: The rule run status (aa:Enabled).
- **Datatype**: boolean
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.rules.enabled}--->

###### id {#action-rules.v2.rules.id}

- **Description**: The rule generated id (aa:RuleID).
- **Datatype**: [Id](#datatypes.Id)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.rules.id}--->

###### name {#action-rules.v2.rules.name}

- **Description**: The rule configuration name.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.rules.name}--->

##### Actions

This entity has no actions.

---

#### action-rules.v2.templates {#action-rules.v2.templates}

- **Description**: Templates that are available to be used in actions.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:action-rules.v2.templates}--->

##### Properties

This entity has no properties.

##### Actions

This entity has no actions.

---

#### action-rules.v2.templates.actions {#action-rules.v2.templates.actions}

- **Description**: Available action to be used in rules.
- **Type**: Collection (Key Property: [id](#action-rules.v2.templates.actions.id))
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:action-rules.v2.templates.actions}--->

##### Properties

###### actionType {#action-rules.v2.templates.actions.actionType}

- **Description**: The type of the action (fixed or unlimited).
- **Datatype**: [ActionType](#datatypes.ActionType)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.templates.actions.actionType}--->

###### id {#action-rules.v2.templates.actions.id}

- **Description**: The ID of the action template.
- **Datatype**: [TemplateId](#datatypes.TemplateId)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.templates.actions.id}--->

###### parameters {#action-rules.v2.templates.actions.parameters}

- **Description**: The parameter array associated with the action.
- **Datatype**: [ParameterArray](#datatypes.ParameterArray)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.templates.actions.parameters}--->

###### recipientTemplate {#action-rules.v2.templates.actions.recipientTemplate}

- **Description**: The ID of the recipient template associated with this action (in case one exists).
- **Datatype**: [TemplateId](#datatypes.TemplateId)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: Yes
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.templates.actions.recipientTemplate}--->

##### Actions

This entity has no actions.

---

#### action-rules.v2.templates.recipients {#action-rules.v2.templates.recipients}

- **Description**: Available recipient templates that can be used in the Axis product.
- **Type**: Collection (Key Property: [id](#action-rules.v2.templates.recipients.id))
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:action-rules.v2.templates.recipients}--->

##### Properties

###### id {#action-rules.v2.templates.recipients.id}

- **Description**: The ID of the recipient template.
- **Datatype**: [TemplateId](#datatypes.TemplateId)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.templates.recipients.id}--->

###### parameters {#action-rules.v2.templates.recipients.parameters}

- **Description**: The parameter array associated with the recipient.
- **Datatype**: [ParameterArray](#datatypes.ParameterArray)
- **Operations**
    - **Get** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:action-rules.v2.templates.recipients.parameters}--->

##### Actions

This entity has no actions.

---

### Data Types

#### ActionConfig {#datatypes.ActionConfig}

- **Description**: The configuration data for the action.
- **Type**: complex
- **Fields**
    - **actionParameters**
        - **Description**: The action parameters
        - **Type**: [ParameterKeyValueArray](#datatypes.ParameterKeyValueArray)
        - **Nullable**: No / **Gettable**: No
    - **recipientId**
        - **Description**: Copy the parameters from a recipient with the provided id.
        - **Type**: [Id](#datatypes.Id)
        - **Nullable**: Yes / **Gettable**: No
    - **recipientParameters**
        - **Description**: The recipient parameters
        - **Type**: [ParameterKeyValueArray](#datatypes.ParameterKeyValueArray)
        - **Nullable**: Yes / **Gettable**: No
    - **template**
        - **Description**: The id of the action template.
        - **Type**: [TemplateId](#datatypes.TemplateId)
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.ActionConfig}--->

#### ActionType {#datatypes.ActionType}

- **Description**: The action type
- **Type**: string
- **Enum Values**: "UNLIMITED", "FIXED"

<!---{Extra-Doc:datatypes.ActionType}--->

#### ActivationConfig {#datatypes.ActivationConfig}

- **Description**: Configuration for triggering action.
- **Type**: complex
- **Fields**
    - **condition**
        - **Description**: Additional conditions that must be fulfilled to trigger the action rule.
        - **Type**: [EventFilterArray](#datatypes.EventFilterArray)
        - **Nullable**: Yes / **Gettable**: No
    - **startEvent**
        - **Description**: A condition that must be fulfilled to trigger the action rule.
        - **Type**: [EventFilter](#datatypes.EventFilter)
        - **Nullable**: Yes / **Gettable**: No
    - **timeout**
        - **Description**: The activation timeout for the action.
        - **Type**: [Timeout](#datatypes.Timeout)
        - **Nullable**: Yes / **Gettable**: No

<!---{Extra-Doc:datatypes.ActivationConfig}--->

#### EventFilter {#datatypes.EventFilter}

- **Description**: The data type of the Web services response array.
- **Type**: complex
- **Fields**
    - **messageContent**
        - **Description**: Message content filter using the ONVIF Message Content Filter dialect. i.e. 'boolean(//SimpleItem[@Name=\"active\" and @Value=\"0\"])'.
        - **Type**: string
        - **Nullable**: Yes / **Gettable**: No
    - **topicExpression**
        - **Description**: Topic expression using the ONVIF Concrete Set dialect. i.e. 'tns1:VideoSource/MotionAlarm'.
        - **Type**: string
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.EventFilter}--->

#### EventFilterArray {#datatypes.EventFilterArray}

- **Description**: Web Services compatible response array.
- **Type**: array
- **Element type**: [EventFilter](#datatypes.EventFilter)
- **Null Value**: No

<!---{Extra-Doc:datatypes.EventFilterArray}--->

#### Id {#datatypes.Id}

- **Description**: A Unique numeric ID.
- **Type**: string
- **Minimum Length**: 1
- **Pattern**: ^[0-9]+$

<!---{Extra-Doc:datatypes.Id}--->

#### Parameter {#datatypes.Parameter}

- **Description**: Parameter information.
- **Type**: complex
- **Fields**
    - **name**
        - **Description**: The name of the parameter.
        - **Type**: string
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.Parameter}--->

#### ParameterArray {#datatypes.ParameterArray}

- **Description**: Array of the parameter names.
- **Type**: array
- **Element type**: [Parameter](#datatypes.Parameter)
- **Null Value**: No

<!---{Extra-Doc:datatypes.ParameterArray}--->

#### ParameterKeyValue {#datatypes.ParameterKeyValue}

- **Description**: The data type of the key value array.
- **Type**: complex
- **Fields**
    - **name**
        - **Description**: The name of the parameter.
        - **Type**: string
        - **Nullable**: No / **Gettable**: No
    - **value**
        - **Description**: The value of the parameter.
        - **Type**: string
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.ParameterKeyValue}--->

#### ParameterKeyValueArray {#datatypes.ParameterKeyValueArray}

- **Description**: A list of parameter names and their corresponding value.
- **Type**: array
- **Element type**: [ParameterKeyValue](#datatypes.ParameterKeyValue)
- **Null Value**: No

<!---{Extra-Doc:datatypes.ParameterKeyValueArray}--->

#### TemplateId {#datatypes.TemplateId}

- **Description**: An id for the object in words separated by dots. I.e in reverse internet domain name format like: 'com.axis.action.fixed.light' or 'com.axis.recipient.http'
- **Type**: string
- **Minimum Length**: 10
- **Maximum Length**: 255
- **Pattern**: ^com\\.axis\\.[A-Za-z0-9\\._-]*\\.[A-Za-z0-9_-]*[^\\.]$

<!---{Extra-Doc:datatypes.TemplateId}--->

#### Timeout {#datatypes.Timeout}

- **Description**: Activation timeout setting, the minimum time that must pass between two consecutive executions of an action.
- **Type**: string
- **Pattern**: ^P(0Y)?(0M)?(\\d+D)?T(\\d+H)?(\\d+M)?(\\d+(\\.\\d+)?S)?$

<!---{Extra-Doc:datatypes.Timeout}--->

