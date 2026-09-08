using System;
using System.IO;

namespace BDVM.Domain;

public static class PlayerIdentity
{
    public static string FromMultiplayerGuid(Guid persistentId)
    {
        if (persistentId == Guid.Empty)
            throw new InvalidDataException("A non-empty authenticated Multiplayer GUID is required for a persistent economic player identity.");
        return "mp-" + persistentId.ToString("N");
    }
}
