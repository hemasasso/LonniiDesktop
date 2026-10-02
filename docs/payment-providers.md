# Payment providers (not wired yet)

Slots are reserved for five non-cash ways of paying. None is implemented, and the settings
screen is hidden until one is.

| Id | Meant for |
|---|---|
| `mobile_money` | Orange Money, MTN MoMo, Moov Money… |
| `card` | Debit and credit cards, any network (Visa, Mastercard…) |
| `google_pay` | Google Pay |
| `apple_pay` | Apple Pay |
| `paypal` | PayPal |

Everything lives in `src/Lonnii.Client/Features/Payments/`.

## Wiring one up

1. Write a class implementing `IPaymentProvider`. `Info` says what Paramètres asks the shop to
   fill in; `ChargeAsync` takes an amount and a reference (the sale number, so a retry cannot
   charge twice) and returns a `PaymentOutcome`.
2. Call `PaymentProviderRegistry.Register(new YourProvider())` at start-up.
3. Make `PaymentProviderRegistry.SettingsVisible` true. **Paramètres → Moyens de paiement**
   appears for admins, with the provider's "Activer" switch now usable.
4. Have the till offer `PaymentProviderRegistry.EnabledAccounts(id)` when its method is chosen, charge the picked account, and record the payment as usual.
   `ModePaiement` already has `google_pay` and `apple_pay` next to `mobile_money` and `carte`.

## What the cashier asks the customer

A provider can declare `CustomerInputs` - details typed at the till for each payment, as opposed
to the shop's saved settings. Mobile Money (Burkina Faso, Orange Money) declares the customer's
phone number and the one-time code (OTP) they generate on their own phone for the exact amount;
the till shows those two boxes, passes them in `PaymentRequest.CustomerValues`, and Orange
answers paid or refused immediately - no public web address or status polling needed. The OTP is
used for that one charge and is never stored or logged.

## Several accounts per provider

Each kind holds any number of accounts (an Orange Money and an MTN MoMo number, two bank
terminals): Paramètres → Moyens de paiement has **+ Ajouter un compte** under each, and every
account has its own name, on/off switch and fields. `ChargeAsync` receives the account to use.
The field list in `PaymentProviderRegistry.Planned` is the form template, written once per kind.

## Where the settings go

`%AppData%\Lonnii\payment-providers.json`, per machine, not in the shared database: it holds
merchant keys, and that database is readable by every till and by Lonnii Business. Fields
marked `Secret` are encrypted with Windows DPAPI for the current user, so the file is useless
if copied to another machine or account.

Lonnii Business already talks to Orange Money Web Payment (`backend/routes/payement.js` in the
React app) - the natural first provider, and a reference for the flow.
