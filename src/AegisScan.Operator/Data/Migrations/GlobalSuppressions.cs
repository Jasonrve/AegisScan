using System.Diagnostics.CodeAnalysis;

// EF Core generates migration InsertData calls with constant array arguments; that's expected
// for generated code and shouldn't be held to the same analyzer bar as hand-written code.
[assembly: SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "EF Core migration-generated code.", Scope = "NamespaceAndDescendants", Target = "~N:AegisScan.KubeOps.Data.Migrations")]
