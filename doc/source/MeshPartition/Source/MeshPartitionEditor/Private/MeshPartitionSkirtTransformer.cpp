// Copyright Epic Games, Inc. All Rights Reserved.

#include "MeshPartitionSkirtTransformer.h"

#include "MeshPartitionDependencyInterface.h"
#include "MeshPartitionEditorModule.h"

namespace UE::MeshPartition
{

bool FMeshSkirtTransformer::Execute(MeshPartition::FTransformerContext& InTransformerContext) const
{
	UE_LOGF(LogMegaMeshEditor, Display, "FMeshSkirtTransformer is deprecated and now a no-op. Enable Mesh Skirt settings on FStaticMeshTransformer instead; existing FMeshSkirtTransformer settings are ignored.");

	return true;
}

void FMeshSkirtTransformer::GatherDependencies(MeshPartition::IDependencyInterface& InDependencies) const
{
}

} // namespace UE::MeshPartition
