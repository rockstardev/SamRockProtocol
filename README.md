# SamRock Protocol for BTCPay Server

This plugin enables quick setup for receiving funds from your BTCPay Server store directly to your self-custodial mobile wallet using the SamRock Protocol.

## Usage

1. Install the plugin by navigating to your BTCPay Server > Server Settings > Plugins, find "SamRock Protocol" in
   Available Plugins, install it, and restart your server.
   a. Ensure that the Boltz plugin is installed and enabled.
   b. SamRock requires BTCPay Server v2.2.0 or newer (plugin dependency resolution introduced in this version).
2. Once installed, navigate to your Store > Plugins > SamRock Protocol
3. You'll be presented with a form where you can select which payment methods you want to set up with your self-custodial wallet:
    * Bitcoin (On-chain)
    * Lightning (via Boltz API)
    * Liquid (On-chain, if Liquid is enabled on your server)
4. Click "Generate QR Code". A unique QR code will be displayed.
5. Scan this QR code with a compatible mobile wallet that supports the SamRock Protocol (e.g., Aqua Wallet). This will configure the necessary wallets on your
   mobile device and link them to your BTCPay Server store for receiving payments.

## Compatible Wallets

* **Aqua Wallet**: ([aqua.net](https://aqua.net))
    * [iOS Download](https://apps.apple.com/us/app/aqua-wallet/id6468594241)
    * [Android Download](https://play.google.com/store/apps/details?id=io.aquawallet.android)

## License

https://github.com/rockstardev/Aqua.BTCPayPlugin/blob/master/LICENSE
