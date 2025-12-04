namespace SS2ExtendedTerritoryBonuses;

using System.Globalization;
using CsvHelper;
using DynamicData;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Noggog;
using OneOf.Types;

class Program
{
    public class KeywordResource
    {
        public required string Keyword { get; set; }
        public required string ResourceAV { get; set; }
        public required string ResourceDescription { get; set; }
        public required int Modifier { get; set; }
        public required string TraitDescription { get; set; }
        public required string TraitEditorID { get; set; }
        public required string TypeName { get; set; }
    }

    public class LocationKeyword
    {
        public required string Name { get; set; }
        public required string LocationEditorID { get; set; }
        public required string Plugin { get; set; }
        public required string Keyword { get; set; }
    }

    static void Main(string[] args)
    {
        // CSVs
        var traitsCSV = new CsvReader(new StreamReader("keywords.csv"), CultureInfo.InvariantCulture)
            .GetRecords<KeywordResource>();

        var locationsCSV = new CsvReader(new StreamReader("locations.csv"), CultureInfo.InvariantCulture)
            .GetRecords<LocationKeyword>();

        string modPrefix = "XTB";
        string modFile = "SS2AOP_ExtendedTerritoryTraits.esp";

        var outgoing = new Fallout4Mod(ModKey.FromFileName(modFile), Fallout4Release.Fallout4);

        List<string> masters = [
            "Fallout4.esm",
            "DLCRobot.esm",
            "DLCCoast.esm",
            "DLCNukaWorld.esm",
            "WorkshopFramework.esm",
            "SS2.esm",
            "SS2_XPAC_Chapter2.esm",
            "SS2_XPAC_Chapter3.esm",
            "SS2AOP_ExtendedTerritoryTraits.esp"
        ];

        var listings = new List<LoadOrderListing>();
        foreach (var master in masters) listings.Add(new(ModKey.FromFileName(master), enabled: true));
        var loadOrder = LoadOrder.Import<IFallout4ModGetter>(listings, GameRelease.Fallout4);

        var env = GameEnvironment.Typical.Builder<IFallout4Mod, IFallout4ModGetter>(GameRelease.Fallout4)
            .WithTargetDataFolder(args[0])
            .WithLoadOrder(loadOrder)
            .WithOutputMod(outgoing)
            .Build();

        ILinkCache linkCache = env.LoadOrder.ToImmutableLinkCache();

        outgoing.IsSmallMaster = true;

        linkCache.TryResolve<IMiscItemGetter>(FormKey.Factory("04E3DD:SS2.esm"), out var traitTemplate);
        if (traitTemplate is null) throw new ArgumentException("Couldn't get trait template");

        linkCache.TryResolve<IMiscItemGetter>(FormKey.Factory("04E3BB:SS2.esm"), out var usageRequirementTemplate);
        if (usageRequirementTemplate is null) throw new ArgumentException("Couldn't get usage requirement template");

        var traitFormList = outgoing.FormLists.AddNew();
        traitFormList.EditorID = $"{modPrefix}_TerritoryTraits";
        traitFormList.Items.Add(FormKey.Factory("04D9AD:SS2.esm"));

        var tempFormList = outgoing.FormLists.AddNew();
        tempFormList.EditorID = $"{modPrefix}_TempFormList";

        // create trait keywords
        foreach (var trait in traitsCSV)
        {
            // check for existing keyword else create new keyword
            IKeywordGetter locationKeyword;
            if (linkCache.TryResolve<IKeywordGetter>(trait.Keyword, out var existingLocationKeyword))
            {
                locationKeyword = existingLocationKeyword;
                tempFormList.Items.Add(existingLocationKeyword.FormKey);
            }
            else
            {
                if (outgoing.Keywords.FirstOrDefault(c => c?.EditorID == trait.Keyword, null) is IKeywordGetter outgoingLocationKeyword)
                {
                    locationKeyword = outgoingLocationKeyword;
                }
                else
                {
                    var newKeyword = outgoing.Keywords.AddNew();
                    newKeyword.EditorID = trait.Keyword;
                    newKeyword.Type = Keyword.TypeEnum.None;
                    locationKeyword = newKeyword;
                }
            }

            IMiscItemGetter usageRequirementMiscItem;
            string usageEditorID = $"{modPrefix}_UsageRequirements_{trait.Keyword}";

            if (outgoing.MiscItems.FirstOrDefault(c => c?.EditorID == usageEditorID, null) is IMiscItemGetter existingMiscItem)
            {
                usageRequirementMiscItem = existingMiscItem;
            }
            else
            {
                // create usage requirements
                var newMiscItem = outgoing.MiscItems.DuplicateInAsNewRecord(usageRequirementTemplate);
                newMiscItem.EditorID = usageEditorID;
                newMiscItem.VirtualMachineAdapter = new VirtualMachineAdapter
                {
                    Scripts = [
                        new ScriptEntry()
                        {
                            Name = "SimSettlementsV2:MiscObjects:UsageRequirements",
                            Properties = [
                                new ScriptStructListProperty(){
                                    Name = "LocationKeywordDataRequirements",
                                    Structs = [
                                        new ScriptEntryStructs(){
                                            Members = [
                                                new ScriptObjectProperty(){
                                                    Name = "KeywordForm",
                                                    Object = locationKeyword.ToLink()
                                                },
                                                new ScriptFloatProperty(){
                                                    Name = "fValue",
                                                    Data = 1
                                                }
                                            ]
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                };
                usageRequirementMiscItem = newMiscItem;
            }

            // find resource AV
            if (!linkCache.TryResolve<IActorValueInformationGetter>(trait.ResourceAV, out var resourceAV))
            {
                throw new ArgumentException($"Did not find resource AV: {trait.ResourceAV}");
            }
            tempFormList.Items.Add(resourceAV.FormKey);

            // create description record
            var newTraitDescriptionMiscItem = outgoing.MiscItems.AddNew();
            newTraitDescriptionMiscItem.EditorID = trait.TraitEditorID+"_Description";
            newTraitDescriptionMiscItem.Name = $"{trait.TypeName} Location Bonus";

            // create trait record
            var newTraitMiscItem = outgoing.MiscItems.DuplicateInAsNewRecord(traitTemplate);
            newTraitMiscItem.EditorID = trait.TraitEditorID;
            newTraitMiscItem.Name = trait.TraitDescription;
            newTraitMiscItem.VirtualMachineAdapter = new VirtualMachineAdapter
            {
                Scripts = [
                    new ScriptEntry()
                    {
                        Name = "SimSettlementsV2:MiscObjects:TerritoryTrait",
                        Properties = [
                            new ScriptBoolProperty(){
                                Name = "bAllowDynamicUse",
                                Data = true
                            },
                            new ScriptStructListProperty(){
                                Name = "DefaultEffectForm01",
                                Structs = [
                                    new ScriptEntryStructs(){
                                        Members = [
                                            new ScriptObjectProperty(){
                                                Name = "BaseForm",
                                                Object = resourceAV.ToLink()
                                            }
                                        ]
                                    }
                                ]
                            },
                            new ScriptObjectProperty(){
                                Name = "DynamicRequirements",
                                Object = usageRequirementMiscItem.ToLink()
                            },
                            new ScriptFloatProperty(){
                                Name = "fDefaultEffectModifier",
                                Data = trait.Modifier
                            },
                            new ScriptIntProperty(){
                                Name = "iType",
                                Data = 3
                            },
                            new ScriptStringProperty(){
                                Name = "sDefaultEffect",
                                Data = "VirtualResourceModifier"
                            },
                            new ScriptObjectProperty(){
                                Name = "TraitDescriptionHolder",
                                Object = newTraitDescriptionMiscItem.ToLink()
                            }
                        ]
                    }
                ]
            };

            traitFormList.Items.Add(newTraitMiscItem.FormKey);
        }

        // get ss2 template quest
        linkCache.TryResolve<IQuestGetter>(FormKey.Factory("00EB1F:SS2.esm"), out var addonQuestTemplate);
        if (addonQuestTemplate is null) throw new ArgumentException("Couldn't get addon quest template");

        // SS2 addon global
        var modVersion = outgoing.Globals.AddNewFloat();
        modVersion.MajorFlags = Global.MajorFlag.Constant;
        modVersion.EditorID = $"{modPrefix}_ModVersion";
        modVersion.Data = 1.0f;
        
        // setup script properties for keyword quest
        ExtendedList<ScriptEntryStructs> locationStructs = [];
        foreach (var location in locationsCSV)
        {
            if (!linkCache.TryResolve<ILocationGetter>(location.LocationEditorID, out var targetLocation))
            {
                Console.WriteLine($"Couldn't find location: {location.LocationEditorID}");
                continue;
            }
            IKeywordGetter locationKeyword;
            if (linkCache.TryResolve<IKeywordGetter>(location.Keyword, out var existingLocationKeyword))
            {
                locationKeyword = existingLocationKeyword;
                tempFormList.Items.Add(existingLocationKeyword.FormKey);
            }
            else
            {
                if (outgoing.Keywords.FirstOrDefault(c => c?.EditorID == location.Keyword, null) is IKeywordGetter outgoingLocationKeyword)
                {
                    locationKeyword = outgoingLocationKeyword;
                }
                else
                {
                    Console.WriteLine($"Couldn't find keyword: {location.LocationEditorID}");
                    continue;
                }
            }

            tempFormList.Items.Add(targetLocation.FormKey);
            tempFormList.Items.Add(locationKeyword.FormKey);

            locationStructs.Add(
                new ScriptEntryStructs(){
                    Members = [
                        new ScriptObjectProperty(){
                            Name = "targetLocation",
                            Object = targetLocation.ToLink()
                        },
                        new ScriptObjectProperty(){
                            Name = "locationKeyword",
                            Object = locationKeyword.ToLink()
                        }
                    ]
                }
            );
        }

        // create quest to apply keywords to locations
        var applyKeywordQuest = outgoing.Quests.DuplicateInAsNewRecord(addonQuestTemplate);
        applyKeywordQuest.EditorID = $"{modPrefix}_ApplyKeywordQuest";
        applyKeywordQuest.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts = [
                new ScriptEntry()
                {
                    Name = "SS2AOP_ExtendedTerritoryTraits:ApplyKeywordQuestScript",
                    Properties = [
                        new ScriptStructListProperty(){
                            Name = "Locations",
                            Structs = locationStructs
                        },
                        new ScriptObjectProperty(){
                            Name = "AddonVersion",
                            Object = modVersion.ToLink()
                        },
                    ]
                }
            ]
        };
        
        // SS2 addon config
        linkCache.TryResolve<IMiscItemGetter>(FormKey.Factory("014B89:SS2.esm"), out var addonConfigTemplate);
        if (addonConfigTemplate is null) throw new ArgumentException("Couldn't get addon config template");

        var addonConfig = outgoing.MiscItems.DuplicateInAsNewRecord(addonConfigTemplate)
            ?? throw new ArgumentException("Couldn't create addon config record"); // cobj item / workshop menu item

        addonConfig.EditorID = $"{modPrefix}_AddonConfig";

        foreach (var script in addonConfig?.VirtualMachineAdapter?.Scripts ?? [])
        {
            if (script.Name!="SimSettlementsV2:MiscObjects:AddonPackConfiguration") continue;

            script.Properties.Add(new ScriptObjectProperty(){ Object = modVersion.ToLink(), Name = "MyVersionNumber" });
            script.Properties.Add(new ScriptStringProperty(){ Data = modFile, Name = "sAddonFilename" });

            var MyItemsProperty  = new ScriptObjectListProperty(){ Objects = [], Name = "MyItems" };
            MyItemsProperty.Objects.Add(new ScriptObjectProperty() { Object = traitFormList.ToLink() });
            script.Properties.Add(MyItemsProperty);
        }

        // SS2 addon quest
        var addonQuest = outgoing.Quests.DuplicateInAsNewRecord(addonQuestTemplate)
            ?? throw new ArgumentException("Couldn't create addon quest record");

        addonQuest.EditorID = $"{modPrefix}_AddonQuest";

        foreach (var script in addonQuest?.VirtualMachineAdapter?.Scripts ?? [])
        {
            if (script.Name!="SimSettlementsV2:quests:AddonPack") continue;

            if (addonConfig is not null) script.Properties.Add(new ScriptObjectProperty(){ Object = addonConfig.ToLink(), Name = "MyAddonConfig" });
        }

        // write file
        outgoing.BeginWrite.ToPath(Path.Combine(args[0], outgoing.ModKey.FileName)).WithLoadOrder(loadOrder).Write();
    }
}