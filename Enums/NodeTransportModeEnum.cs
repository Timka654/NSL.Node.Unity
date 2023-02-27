using System;

[Flags]
public enum NodeTransportModeEnum
{
    P2POnly = 1,
    ProxyOnly = 2,
    All = P2POnly | ProxyOnly
}