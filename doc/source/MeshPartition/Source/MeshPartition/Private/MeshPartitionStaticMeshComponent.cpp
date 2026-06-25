// Copyright Epic Games, Inc. All Rights Reserved.

#include "MeshPartitionStaticMeshComponent.h"

namespace UE::MeshPartition
{
UMeshPartitionStaticMeshComponent::UMeshPartitionStaticMeshComponent(const FObjectInitializer& ObjectInitializer)
{
	bLumenHeightfield = true;
}

void UMeshPartitionStaticMeshComponent::OnRegister()
{
	Super::OnRegister();
}

void UMeshPartitionStaticMeshComponent::OnUnregister()
{
	Super::OnUnregister();
}
} // namespace UE::MeshPartition