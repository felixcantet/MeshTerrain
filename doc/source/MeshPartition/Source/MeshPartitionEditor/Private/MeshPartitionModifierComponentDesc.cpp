// Copyright Epic Games, Inc. All Rights Reserved.

#include "MeshPartitionModifierComponentDesc.h"
#include "MeshPartitionModifierComponent.h"
#include "WorldPartition/WorldPartitionActorDescArchive.h"
#include "MeshPartitionModule.h"
#include "MeshPartitionEditorModule.h"

namespace UE::MeshPartition
{

uint32 FWorldPartitionModifierComponentDesc::GetSizeOf() const
{
	return sizeof(*this);
}

void FWorldPartitionModifierComponentDesc::Init(const UActorComponent* InComponent)
{
	FWorldPartitionComponentDesc::Init(InComponent);

	const MeshPartition::UModifierComponent* Modifier = CastChecked<MeshPartition::UModifierComponent>(InComponent);
	MeshPartition::FModifierDesc Desc(*Modifier);

	// We must store the bounds of the component in local space so that delta serialization based on the default of the component
	// can properly function. If not, the bounds will always differ from the default and will not correctly respond to changes in
	// the base class which affect the modifier bounds.
	const FTransform ComponentToWorld = Modifier->GetComponentToWorld();

	// Component descriptors can be created for components without owners when they are BP templates.
	// In this case it's fine to serialize an Identity transform because we don't delta serialize the component to actor transform.
	if (!Modifier->GetOwner())
	{
		ComponentToActorTransform = FTransform::Identity;
	}
	else
	{
		ComponentToActorTransform = Modifier->GetComponentTransform().GetRelativeTransform(Modifier->GetOwner()->GetActorTransform());
	}
	LocalBounds = Desc.Bounds.InverseTransformBy(ComponentToWorld);
	
	ModifierPath = Desc.ModifierPath;
	Type = Desc.Type;
	Priority = Desc.Priority;
	MegaMeshGuid = Desc.MegaMeshGuid;
	BaseGrowth = Desc.BaseGrowth;
	Complexity = Desc.Complexity;
	ComplexityMultiplier = Desc.ComplexityMultiplier;
	bIsContiguous = Desc.bIsContiguous;
	bIsDisabled = Desc.bIsDisabled;
	bIsBase = Desc.bIsBase;

	bHasComponentToActorTransform = true;
}

void FWorldPartitionModifierComponentDesc::Serialize(FArchive& Ar)
{
	FWorldPartitionComponentDesc::Serialize(Ar);

	Ar.UsingCustomVersion(MeshPartition::FCustomVersion::GUID);

	if (Ar.CustomVer(MeshPartition::FCustomVersion::GUID) >= MeshPartition::FCustomVersion::ComponentToActorTransform)
	{
		Ar << ComponentToActorTransform;
		bHasComponentToActorTransform = true;
	}
	else
	{
		PRAGMA_DISABLE_DEPRECATION_WARNINGS
		Ar << ComponentTransform;
		PRAGMA_ENABLE_DEPRECATION_WARNINGS
		bHasComponentToActorTransform = false;
	}
	Ar << ModifierPath; // modifier path is technically serializable from the parent actor desc + component desc but there are edge cases and this is far simpler.
	Ar << MegaMeshGuid;

	Ar << TDeltaSerialize<FName>(Type);
	Ar << TDeltaSerialize<double>(Priority);
	Ar << TDeltaSerialize<FBox>(LocalBounds);
	Ar << TDeltaSerialize<MeshPartition::FBaseGrowth>(BaseGrowth);
	Ar << TDeltaSerialize<double>(Complexity);
	Ar << TDeltaSerialize<float>(ComplexityMultiplier);
	Ar << TDeltaSerialize<bool>(bIsContiguous);
	Ar << TDeltaSerialize<bool>(bIsDisabled);
		
	// Before this version, modifier "base-ness" was defined by having a Type == "Base".
	if (Ar.CustomVer(MeshPartition::FCustomVersion::GUID) >= MeshPartition::FCustomVersion::VirtualIsBaseModifier)
	{
		Ar << TDeltaSerialize<bool>(bIsBase);
	}
	else
	{
		if (Ar.IsLoading())
		{
			bIsBase = Type == TEXT("Base");
		}
	}
}

} // namespace UE::MeshPartition
