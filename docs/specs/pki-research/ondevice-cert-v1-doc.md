# Certificate Management (Version 1.1.2)

## Overview

- **Short Description**: Management of X.509 certificates.
- **ID**: cert
- **Version**: 1.1.2
- **State**: Released

This API is based on the **Device Configuration API** framework. For guidance on how to use these APIs, please refer to the `Device Configuration APIs` section in the VAPIX Library.


<!---{Extra-Doc:api-description}--->

<!---{Extra-Doc:use-cases}--->

## API Definition

### Structure

```text
cert.v1 (Root Entity)
    ├── create_certificate (Action)
    ├── install_from_pkcs12 (Action)
    ├── ca_certificates (Entity Collection)
        ├── alias (Property)
        ├── certificate (Property)
    ├── certificates (Entity Collection)
        ├── alias (Property)
        ├── certificate (Property)
        ├── keystore (Property)
        ├── private_key (Property)
        ├── get_csr (Action)
    ├── keystores (Entity Collection)
        ├── certifications (Property)
        ├── id (Property)
        ├── key_types (Property)
        ├── security_level (Property)
    ├── settings (Entity)
        ├── keystore (Property)
```

### Entities

#### cert.v1 {#cert.v1}

- **Description**: The certificate management root object.
- **Type**: Singleton
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:cert.v1}--->

##### Properties

This entity has no properties.

##### Actions

###### create_certificate  {#cert.v1.create_certificate}
- **Description**: Create a self-signed certificate.
- **Request Datatype**: [create_certificate_input](#datatypes.create_certificate_input)
- **Response Datatype**: Empty Object
- **Trigger Permissions**: admin

<!---{Extra-Doc:cert.v1.create_certificate}--->

###### install_from_pkcs12  {#cert.v1.install_from_pkcs12}
- **Description**: Install a server/client certificate with a private key from a password protected encrypted PKCS12 archive.
- **Request Datatype**: [install_from_pkcs12_input](#datatypes.install_from_pkcs12_input)
- **Response Datatype**: Empty Object
- **Trigger Permissions**: admin

<!---{Extra-Doc:cert.v1.install_from_pkcs12}--->

---

#### cert.v1.ca_certificates {#cert.v1.ca_certificates}

- **Description**: Installed CA certificates.
- **Type**: Collection (Key Property: [alias](#cert.v1.ca_certificates.alias))
- **Operations**
    - **Get**
    - **Set**
        - **Properties**: certificate
    - **Add** (**Permissions**: admin)
        - **Required properties**: alias, certificate
        - **Optional properties**: 
    - **Remove** (**Permissions**: admin)
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:cert.v1.ca_certificates}--->

##### Properties

###### alias {#cert.v1.ca_certificates.alias}

- **Description**: Identifier for this certificate entry.
- **Datatype**: [alias](#datatypes.alias)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert.v1.ca_certificates.alias}--->

###### certificate {#cert.v1.ca_certificates.certificate}

- **Description**: The X.509 certificate in PEM format. Can be replaced with a newer version.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert.v1.ca_certificates.certificate}--->

##### Actions

This entity has no actions.

---

#### cert.v1.certificates {#cert.v1.certificates}

- **Description**: Installed server and client certificates.
- **Type**: Collection (Key Property: [alias](#cert.v1.certificates.alias))
- **Operations**
    - **Get**
    - **Set**
        - **Properties**: certificate
    - **Add** (**Permissions**: admin)
        - **Required properties**: alias, certificate, private_key
        - **Optional properties**: keystore
    - **Remove** (**Permissions**: admin)
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:cert.v1.certificates}--->

##### Properties

###### alias {#cert.v1.certificates.alias}

- **Description**: Identifier for this certificate entry.
- **Datatype**: [alias](#datatypes.alias)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert.v1.certificates.alias}--->

###### certificate {#cert.v1.certificates.certificate}

- **Description**: The X.509 certificate in PEM format. Can be replaced with a freshly signed version for the same private key.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert.v1.certificates.certificate}--->

###### keystore {#cert.v1.certificates.keystore}

- **Description**: The keystore this private key is protected by. Can not be changed once the private key has been protected.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert.v1.certificates.keystore}--->

###### private_key {#cert.v1.certificates.private_key}

- **Description**: The private key in PEM format. The key can only be used to add a certificate and can not be extracted again once it is protected.
- **Datatype**: string
- **Operations**
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert.v1.certificates.private_key}--->

##### Actions

###### get_csr  {#cert.v1.certificates.get_csr}
- **Description**: Get a PKCS10 certificate signing request in PEM format.
- **Request Datatype**: [get_csr_input](#datatypes.get_csr_input)
- **Response Datatype**: string
- **Trigger Permissions**: admin

<!---{Extra-Doc:cert.v1.certificates.get_csr}--->

---

#### cert.v1.keystores {#cert.v1.keystores}

- **Description**: List of keystores available on the system.
- **Type**: Collection (Key Property: [id](#cert.v1.keystores.id))
- **Operations**
    - **Get**
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:cert.v1.keystores}--->

##### Properties

###### certifications {#cert.v1.keystores.certifications}

- **Description**: The list of security standards of the subsystem that this keystore is certified with.
- **Datatype**: [keystore_certifications](#datatypes.keystore_certifications)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert.v1.keystores.certifications}--->

###### id {#cert.v1.keystores.id}

- **Description**: Keystore identifier.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert.v1.keystores.id}--->

###### key_types {#cert.v1.keystores.key_types}

- **Description**: A list of available key types offered by the keystore.
- **Datatype**: [keystore_key_types_list](#datatypes.keystore_key_types_list)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert.v1.keystores.key_types}--->

###### security_level {#cert.v1.keystores.security_level}

- **Description**: The level of protection for private keys that this keystore offers.
- **Datatype**: [keystore_seclevel](#datatypes.keystore_seclevel)
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert.v1.keystores.security_level}--->

##### Actions

This entity has no actions.

---

#### cert.v1.settings {#cert.v1.settings}

- **Description**: Global settings for certificate management.
- **Type**: Singleton
- **Operations**
    - **Get**
    - **Set**
        - **Properties**: keystore
- **Attributes**
    - **Dynamic Support**: No

<!---{Extra-Doc:cert.v1.settings}--->

##### Properties

###### keystore {#cert.v1.settings.keystore}

- **Description**: The selected keystore. Newly installed or generated private keys are protected in it by default.
- **Datatype**: string
- **Operations**
    - **Get** (**Permissions:** admin, operator, viewer)
    - **Set** (**Permissions:** admin)
- **Attributes**
    - **Nullable**: No
    -  **Dynamic Support**: No / **Dynamic Enum**: No / **Dynamic Range**: No

<!---{Extra-Doc:cert.v1.settings.keystore}--->

##### Actions

This entity has no actions.

---

### Data Types

#### alias {#datatypes.alias}

- **Description**: The client-defined alias of the certificate.
- **Type**: string
- **Minimum Length**: 1
- **Maximum Length**: 128

<!---{Extra-Doc:datatypes.alias}--->

#### create_certificate_input {#datatypes.create_certificate_input}

- **Description**: Certificate creation action parameters.
- **Type**: complex
- **Fields**
    - **alias**
        - **Description**: Identifier for this certificate.
        - **Type**: [alias](#datatypes.alias)
        - **Nullable**: No / **Gettable**: No
    - **key_type**
        - **Description**: The key algorithm and strength that should be used. For RSA keys this is RSA-bitlength, e.g. `RSA-2048`. For EC keys this is `EC-P256`, `EC-P384` and `EC-P521` for the widely used NIST curves supported by the TLS standard. Not all keystores support all key types. If no key type is specified, a default will be used. This will be `EC-P256` for most keystores.
        - **Type**: string
        - **Nullable**: Yes / **Gettable**: No
    - **keystore**
        - **Description**: The keystore in which this private key should be generated. If not specified, the configured default keystore is used.
        - **Type**: string
        - **Nullable**: Yes / **Gettable**: No
    - **subject**
        - **Description**: Subject distinguished name, defined in RFC 4514.
        - **Type**: string
        - **Nullable**: No / **Gettable**: No
    - **subject_alt_names**
        - **Description**: Subject alternative names, each prefixed with either "IP:" or "DNS:", e.g. "DNS:example.org".
        - **Type**: [san_list](#datatypes.san_list)
        - **Nullable**: Yes / **Gettable**: No
    - **valid_from**
        - **Description**: The validity start date for the certificate. Default value is today. Number of seconds since Unix epoch, 1970-01-01 00:00:00 UTC.
        - **Type**: integer
        - **Nullable**: Yes / **Gettable**: No
    - **valid_to**
        - **Description**: The validity end date for the certificate. Default value is one year from today's date. Number of seconds since Unix epoch, 1970-01-01 00:00:00 UTC.
        - **Type**: integer
        - **Nullable**: Yes / **Gettable**: No

<!---{Extra-Doc:datatypes.create_certificate_input}--->

#### get_csr_input {#datatypes.get_csr_input}

- **Description**: Certificate Signing Request action parameters.
- **Type**: complex
- **Fields**
    - **subject**
        - **Description**: Optional new subject DN, which if used, will replace the original.
        - **Type**: string
        - **Nullable**: Yes / **Gettable**: No
    - **subject_alt_names**
        - **Description**: Optional new list of alternative names, which if used, will replace the original.
        - **Type**: [san_list](#datatypes.san_list)
        - **Nullable**: Yes / **Gettable**: No

<!---{Extra-Doc:datatypes.get_csr_input}--->

#### install_from_pkcs12_input {#datatypes.install_from_pkcs12_input}

- **Description**: PKCS12 installation action parameters.
- **Type**: complex
- **Fields**
    - **alias**
        - **Description**: New identifier for this certificate.
        - **Type**: [alias](#datatypes.alias)
        - **Nullable**: No / **Gettable**: No
    - **keystore**
        - **Description**: The keystore protecting the private key. The configured default keystore will be used if this parameter is not specified.
        - **Type**: string
        - **Nullable**: Yes / **Gettable**: No
    - **passphrase**
        - **Description**: The passphrase for the PKCS12 archive.
        - **Type**: string
        - **Nullable**: No / **Gettable**: No
    - **pkcs12**
        - **Description**: The raw data for the PKCS12 archive, base64-encoded.
        - **Type**: string
        - **Nullable**: No / **Gettable**: No

<!---{Extra-Doc:datatypes.install_from_pkcs12_input}--->

#### keystore_certifications {#datatypes.keystore_certifications}

- **Description**: A list of security standards for the subsystem that a keystore is certified with. E.g. "FIPS 140-2 Level 2".
- **Type**: array
- **Element type**: string
- **Null Value**: No

<!---{Extra-Doc:datatypes.keystore_certifications}--->

#### keystore_key_types {#datatypes.keystore_key_types}

- **Type**: string
- **Enum Values**: "EC-P256", "EC-P384", "EC-P521", "RSA-1024", "RSA-2048", "RSA-3072", "RSA-4096"

<!---{Extra-Doc:datatypes.keystore_key_types}--->

#### keystore_key_types_list {#datatypes.keystore_key_types_list}

- **Description**: A list of available key types offered by a keystore.
- **Type**: array
- **Element type**: [keystore_key_types](#datatypes.keystore_key_types)
- **Null Value**: No

<!---{Extra-Doc:datatypes.keystore_key_types_list}--->

#### keystore_seclevel {#datatypes.keystore_seclevel}

- **Description**: The protection level of private keys offered by a keystore.
- **Type**: string
- **Enum Values**: "SOFTWARE", "TRUSTED_ENVIRONMENT", "STRONG"

<!---{Extra-Doc:datatypes.keystore_seclevel}--->

#### san_list {#datatypes.san_list}

- **Description**: Subject alternative names, each prefixed with either "IP:" or "DNS:", e.g. "DNS:example.org".
- **Type**: array
- **Element type**: string
- **Null Value**: No

<!---{Extra-Doc:datatypes.san_list}--->

