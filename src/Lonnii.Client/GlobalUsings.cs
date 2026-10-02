// Each feature owns its own namespace (Features/<Name>); modules open each other's dialogs and
// share a few helpers, so the feature namespaces are imported once here.
global using Lonnii.Client.Features.Amortissement;
global using Lonnii.Client.Features.Audit;
global using Lonnii.Client.Features.Auth;
global using Lonnii.Client.Features.Bilan;
global using Lonnii.Client.Features.Caisse;
global using Lonnii.Client.Features.Charges;
global using Lonnii.Client.Features.Marges;
global using Lonnii.Client.Features.Members;
global using Lonnii.Client.Features.Parametres;
global using Lonnii.Client.Features.Programme;
global using Lonnii.Client.Features.Stock;
global using Lonnii.Client.Features.Ventes;
global using Lonnii.Client.Common;
global using Lonnii.Client.Common.Controls;
