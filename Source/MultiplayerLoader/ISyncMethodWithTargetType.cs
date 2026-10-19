using System;
using Multiplayer.API;

namespace MultiplayerLoader;

public interface ISyncMethodWithTargetType : ISyncMethod
{
    Type TargetType { get; }
}
