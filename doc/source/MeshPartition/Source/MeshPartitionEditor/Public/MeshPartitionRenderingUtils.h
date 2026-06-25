// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "RenderCaptureInterface.h"

namespace UE::MeshPartition
{

class FRenderCaptureManager
{
	int NextCaptureCounter = 0;
	TUniquePtr<RenderCaptureInterface::FScopedCapture> CurrentCapture;
public:
	static FRenderCaptureManager& Get();

	static void CaptureNextBatch();
	static void	BeginCapture(FRDGBuilder& InGraphBuilder);
	static void EndCapture();
};

} // namespace UE::MeshPartition
