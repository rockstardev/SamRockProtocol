dotnet publish -o ./publish
dotnet publish ../../submodules/boltz/BTCPayServer.Plugins.Boltz -o ./publish
dotnet run --project ../../submodules/btcpayserver/BTCPayServer.PluginPacker ./publish SamRockProtocol ./release
