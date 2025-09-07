# SamRock Protocol for BTCPay Server

This plugin enables quick setup for receiving funds from your BTCPay Server store directly to your self-custodial mobile wallet using the SamRock Protocol.

## Usage

1. Install the plugin by navigating to your BTCPay Server > Server Settings > Plugins, find "SamRock Protocol" in
   Available Plugins, install it, and restart your server.
   a. Ensure that the Boltz plugin is installed and enabled.
   b. SamRock requires BTCPay Server v2.1.6 or newer (plugin dependency resolution introduced in this version).
2. Once installed, navigate to your Store > Plugins > SamRock Protocol
3. You'll be presented with a form where you can select which payment methods you want to set up with your self-custodial wallet:
    * Bitcoin (On-chain)
    * Lightning (via Boltz API)
    * Liquid (On-chain, if Liquid is enabled on your server)
4. Click "Generate QR Code". A unique QR code will be displayed.
5. Scan this QR code with a compatible mobile wallet that supports the SamRock Protocol (e.g., Aqua Wallet). This will configure the necessary wallets on your
   mobile device and link them to your BTCPay Server store for receiving payments.


## Protocol specification

SamRock Protocol is triggered by selecting payment methods to setup on BTCPay Server and generating QR code. The QR encodes a one-time setup URL:

`https://<btcpayserver>/plugins/{storeId}/samrock/protocol?setup=btc,lbtc,btcln,&otp=<OTP>`

Optionally, protocol may signal to wallet to upload all payment methods using `setup=all` querystring parameter.
If `setup` parameter is omitted, wallet should default to `setup=all` and sending all supported payment methods.

The wallet must send a `POST` request to this URL with form field `json` containing setup details.

### Request

```http
POST /plugins/{storeId}/samrock/protocol?otp=<OTP>
Content-Type: application/x-www-form-urlencoded

json={...}
```

### JSON structure

```json
{
  "BTC": {
    "Descriptor": "wpkh([8f681564/84'/0'/0']xpub6CUGRU.../0/*)#8m68c9t7"
  },
  "LBTC": {
    "Descriptor": "ct(slip77(4a3b...ff9),elsh(wpkh([d34db33f/84'/1776'/0']xpub6FUGRU.../0/*)))"
  },
  "BTCLN": {
    "Type": "Boltz",
    "LBTC": {
      "Descriptor": "..."
    }
  }
}
```

* **BTC.Descriptor** – Standard output descriptor (wpkh, pkh, sh(wpkh), tr).
* **LBTC.Descriptor** – Confidential descriptor with `slip77` blinding key and embedded descriptor.
* **BTCLN** – Currently only `"Type": "Boltz"` is supported, with associated Liquid data.

### Response

```json
{
  "Success": true,
  "Message": "Wallet setup successfully.",
  "Result": {
    "BTC":    { "Success": true },
    "LBTC":   { "Success": true },
    "BTC_LN": { "Success": true }
  }
}
```

Errors include `"Success": false` with `"Message"` and `"Error"` fields.

## Compatible Wallets

* **Aqua Wallet**: ([aqua.net](https://aqua.net))
    * [iOS Download](https://apps.apple.com/us/app/aqua-wallet/id6468594241)
    * [Android Download](https://play.google.com/store/apps/details?id=io.aquawallet.android)

## License

https://github.com/rockstardev/Aqua.BTCPayPlugin/blob/master/LICENSE
