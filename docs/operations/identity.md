# Enterprise identity lifecycle

AegisScan currently supports OIDC bearer authentication. SAML and SCIM are
not implemented in this release. For SAML-only identity providers, configure
an enterprise identity gateway that translates SAML to OIDC. Provision and
deprovision users through the gateway's group management; AegisScan maps the
configured role claim to Admin, Operator, or Viewer on each request.

Deprovisioning is effective when the issuer stops minting valid tokens. Rotate
or revoke gateway sessions according to the identity provider's controls.
