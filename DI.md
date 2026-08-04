Corrected

┌──────────────┬──────────────────────────────────────┬───────────────────────┐
│              │             Your summary             │        Actual         │
├──────────────┼──────────────────────────────────────┼───────────────────────┤
│ Resource     │ Microsoft.CognitiveServices/accounts │ ✅ correct            │
│ type         │                                      │                       │
├──────────────┼──────────────────────────────────────┼───────────────────────┤
│ Kind         │ FormRecognizer                       │ ❌ AIServices         │
├──────────────┼──────────────────────────────────────┼───────────────────────┤
│              │                                      │ ❌ Foundry resource   │
│ Architecture │ AI Foundry Hub                       │ (resource-based, no   │
│              │                                      │ hub)                  │
├──────────────┼──────────────────────────────────────┼───────────────────────┤
│ SKU          │ S0                                   │ ✅ correct            │
└──────────────┴──────────────────────────────────────┴───────────────────────┘

Kind: AIServices, not FormRecognizer

kind: FormRecognizer is the standalone single-service Document Intelligence resource — the fallback this project explicitly rejected. Deploying it would create a second resource next to aif-eugo-swc, with its own subdomain, its own private endpoint, its own role assignment, and its own federation. That undoes the consolidation decision.

Your instinct isn't baseless though — FormRecognizer is a real identifier in this stack, just not as kind. It's the sub-service name inside the AIServices account:

- properties.endpoints["FormRecognizer"] → https://aif-eugo-swc.cognitiveservices.azure.com/
- the RBAC data action Microsoft.CognitiveServices/accounts/FormRecognizer/documentmodels/read

Same string, different slot: it names the capability, not the resource kind.

No hub involved

An AI Foundry hub is a different resource type entirely — Microsoft.MachineLearningServices/workspaces with kind: Hub, the older hub-based architecture. You have none: az resource list for that type returns empty across the subscription.

What you have is the newer resource-based Foundry: allowProjectManagement: true on the account, with nine accounts/projects children including proj-eugo-docint-{dev,stage,prod}-swc.

And worth being clear — docint doesn't use any of those projects. The DI call goes straight to the account data plane at https://aif-eugo-swc.cognitiveservices.azure.com/ with the pod's own credential. Projects matter for the agent work in eugo-web; they're not in docint's path at all.

The summary as it should read

▎ Resource provider: Microsoft.CognitiveServices/accounts, kind: AIServices, SKU S0, custom subdomain required (aif-eugo-swc).
▎ Architecture: resource-based Foundry — no hub, no project on docint's path. Document Intelligence is a built-in capability of the account, reached on the FormRecognizer endpoint; prebuilt-layout is named in the request and never deployed.
▎ Already provisioned. Nothing to add for DI. Outstanding: a Cognitive Services User role assignment for docint's workload identity.