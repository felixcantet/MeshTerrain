// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
#include "Math/Box.h"
#include "Math/IntVector.h"

class UPreviewGeometry;

namespace UE::MeshPartition
{
	void CreatePreviewGridLines(const FBox& Box, FIntVector Dims, const FString& LineGroupName, UPreviewGeometry* PreviewGeometry);
}
