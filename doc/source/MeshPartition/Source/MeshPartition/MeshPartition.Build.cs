// Copyright Epic Games, Inc. All Rights Reserved.

using UnrealBuildTool;

public class MeshPartition : ModuleRules
{
	public MeshPartition(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = ModuleRules.PCHUsageMode.UseExplicitOrSharedPCHs;
		bAllowUETypesInNamespaces = true;

		PublicIncludePaths.AddRange(
			new string[] {
			}
			);
		
		PrivateIncludePaths.AddRange(
			new string[] {
			}
			);
		
		PublicDependencyModuleNames.AddRange(
			new string[]
			{
				"Core",
			}
			);
		
		PrivateDependencyModuleNames.AddRange(
			new string[]
			{
				"CoreUObject",
				"DeveloperSettings",
				"DynamicMesh",
				"Engine",
				"GeometryCore",
				"MeshConversion",
				"MeshDescription",
				"PhysicsCore", // for collision mesh serialization
				"RenderCore",
				"Renderer",
				"RHI"
			}
			);
		
		DynamicallyLoadedModuleNames.AddRange(
			new string[]
			{
			}
			);

		if (Target.bBuildEditor)
		{
			PrivateDependencyModuleNames.AddRange(
				new string[]
				{
					"DataLayerEditor"
				}
			);
		}
	}
}
