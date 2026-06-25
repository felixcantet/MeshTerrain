// Copyright Epic Games, Inc. All Rights Reserved.
#pragma once

#include "CoreMinimal.h"

namespace UE::MeshPartition
{
	struct FStaticMeshDescriptor
	{
		FName CollisionProfileName = NAME_None;
		
		bool bCanEverAffectNavigation = true;
		
		FBox2f UVRegion = FBox2f(FVector2f::Zero(), FVector2f::One());
	};
}
