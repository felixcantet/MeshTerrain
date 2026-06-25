// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "Templates/SharedPointer.h"

class ISlateStyle;

class FMegaMeshEditorUIStyle
{
public:
	static void Initialize();
	static void Shutdown();

	/** Reloads textures used by slate renderer */
	static void ReloadTextures();

	/** @return The Slate style set for the Shooter game */
	static TSharedRef<ISlateStyle> Get();

	static FName GetStyleSetName();

private:
	static TSharedRef<class FSlateStyleSet> Create();
	static FString InContent(const FString& InRelativePath, const ANSICHAR* InExtension);
private:
	static TSharedPtr<class FSlateStyleSet> StyleInstance;
};
