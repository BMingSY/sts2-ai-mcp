using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Debug;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using STS2AIMCP.Server;

namespace STS2AIMCP.Game;

internal static class DecisionWindowService
{
    public const string ProtocolVersion = "2026-07-04-v2-draft";

    private static Dictionary<string, Dictionary<string, Dictionary<string, object?>>>? _modelDbGameDataIndex;

    private const int DecisionVersion = 1;
    private const string DefaultProfile = "ai_safe";
    private const string ModVersion = "0.1.0";
    private static readonly TimeSpan CombatStableDelay = TimeSpan.FromMilliseconds(500);
    private static readonly object CombatStabilityGate = new();
    private static string? _lastCombatStabilitySignature;
    private static DateTime _lastCombatStabilityChangedUtc = DateTime.MinValue;

    private static readonly Regex DealDamageRegex = new(
        @"\bDeal\s+(?<amount>\d+)\s+damage",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GainBlockRegex = new(
        @"\bGain\s+(?<amount>\d+)\s+Block",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TimesRegex = new(
        @"\b(?<amount>\d+|X)\s+times?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ApplyPowerRegex = new(
        @"\b(?:Apply|Gain)\s+(?<amount>\d+)\s+(?<power>[A-Za-z][A-Za-z '\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static DecisionCurrentPayload GetCurrent(DecisionRequestOptions options)
    {
        var state = GameStateService.BuildStatePayload();
        if (!TryBuildDecision(state, options, out var decision, out var reason))
        {
            return new DecisionCurrentPayload
            {
                available = false,
                reason = reason,
                screen = state.screen,
                last_transition = state.screen,
                raw_state = options.include_raw_state ? state : null
            };
        }

        return new DecisionCurrentPayload
        {
            available = true,
            decision = decision,
            raw_state = options.include_raw_state ? state : null
        };
    }

    public static async Task<DecisionActResponsePayload> ActAsync(DecisionActRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.decision_id))
        {
            throw new ApiException(400, "invalid_request", "decision_id is required.");
        }

        if (string.IsNullOrWhiteSpace(request.action_id))
        {
            throw new ApiException(400, "invalid_request", "action_id is required.");
        }

        var current = GetCurrent(new DecisionRequestOptions
        {
            profile = DefaultProfile,
            include_raw_state = false,
            include_relevant_game_data = false
        });

        if (!current.available || current.decision == null)
        {
            throw new ApiException(
                409,
                "decision_unavailable",
                "No stable decision is currently available.",
                new
                {
                    reason = current.reason,
                    screen = current.screen
                },
                retryable: true);
        }

        var decision = current.decision;
        if (!string.Equals(decision.decision_id, request.decision_id, StringComparison.Ordinal))
        {
            throw new ApiException(
                409,
                "stale_decision",
                "Decision window changed. Call wait_for_decision again.",
                new
                {
                    expected_decision_id = request.decision_id,
                    actual_decision_id = decision.decision_id
                },
                retryable: true);
        }

        var choice = decision.choices.FirstOrDefault(candidate =>
            string.Equals(candidate.action_id, request.action_id, StringComparison.Ordinal));

        if (choice == null)
        {
            throw new ApiException(
                409,
                "invalid_action",
                "action_id does not exist in this decision.",
                new
                {
                    decision_id = request.decision_id,
                    action_id = request.action_id
                });
        }

        if (IsActionHiddenByProfile(choice, DefaultProfile))
        {
            throw new ApiException(
                409,
                "action_not_allowed",
                "Action is hidden by the active profile.",
                new
                {
                    decision_id = request.decision_id,
                    action_id = request.action_id,
                    profile = DefaultProfile
                });
        }

        var actionRequest = BuildActionRequest(choice, request);
        var actionResponse = await GameActionService.ExecuteAsync(actionRequest);
        DecisionWindowPayload? nextDecision = null;

        if (actionResponse.stable &&
            TryBuildDecision(actionResponse.state, new DecisionRequestOptions
            {
                profile = DefaultProfile,
                include_raw_state = false,
                include_relevant_game_data = false
            }, out var builtNextDecision, out _) &&
            builtNextDecision.decision_id != decision.decision_id)
        {
            nextDecision = builtNextDecision;
        }

        return new DecisionActResponsePayload
        {
            action_id = choice.action_id,
            kind = choice.kind,
            status = actionResponse.status,
            stable = actionResponse.stable,
            message = actionResponse.message,
            previous_decision_id = decision.decision_id,
            next_decision = nextDecision
        };
    }

    public static GameDataLookupPayload LookupGameData(GameDataLookupRequest request)
    {
        var state = GameStateService.BuildStatePayload();
        var indexes = BuildGameDataIndex(state);
        var requestedFields = request.fields?
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .Select(field => field.Trim())
            .ToArray() ?? Array.Empty<string>();
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var item in request.items ?? Array.Empty<GameDataLookupItemRequest>())
        {
            var collection = item.collection?.Trim() ?? string.Empty;
            var id = item.id?.Trim() ?? string.Empty;
            var key = $"{collection}:{id}";
            if (string.IsNullOrWhiteSpace(collection) || string.IsNullOrWhiteSpace(id))
            {
                result[key] = null;
                continue;
            }

            if (!indexes.TryGetValue(collection, out var collectionIndex) ||
                !TryGetIndexedItem(collectionIndex, id, out var value))
            {
                result[key] = null;
                continue;
            }

            result[key] = FilterFields(value, requestedFields);
        }

        return new GameDataLookupPayload
        {
            items = result,
            metadata = new Dictionary<string, object?>
            {
                ["game_version"] = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown",
                ["mod_version"] = ModVersion,
                ["data_source"] = "loaded_game_model",
                ["exported_at_utc"] = DateTime.UtcNow.ToString("O"),
                ["content_hash"] = null
            }
        };
    }

    public static GameDataExportPayload ExportGameData()
    {
        var indexes = BuildModelDbGameDataIndex();
        var collections = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);

        foreach (var (collection, index) in indexes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var exported = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var item in index.Values)
            {
                if (!item.TryGetValue("id", out var idValue) || string.IsNullOrWhiteSpace(idValue?.ToString()))
                {
                    continue;
                }

                exported[idValue!.ToString()!] = item;
            }

            collections[collection] = exported;
        }

        return new GameDataExportPayload
        {
            collections = collections,
            metadata = new Dictionary<string, object?>
            {
                ["game_version"] = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown",
                ["mod_version"] = ModVersion,
                ["data_source"] = "loaded_game_model",
                ["exported_at_utc"] = DateTime.UtcNow.ToString("O"),
                ["content_hash"] = null
            }
        };
    }

    private static bool TryBuildDecision(
        GameStatePayload state,
        DecisionRequestOptions options,
        out DecisionWindowPayload decision,
        out string reason)
    {
        decision = new DecisionWindowPayload();
        reason = string.Empty;

        var profile = NormalizeProfile(options.profile);
        var phase = ResolvePhase(state);
        var choices = BuildChoices(state, phase, profile);

        if (phase == "unknown")
        {
            ResetCombatStability();
            reason = "state_unstable";
            return false;
        }

        if (phase != "combat")
        {
            ResetCombatStability();
        }

        if (phase == "combat" && IsCombatDecisionUnstable(state, choices))
        {
            reason = "state_unstable";
            return false;
        }

        if (!HasMeaningfulChoices(choices) && phase != "game_over")
        {
            reason = "decision_unavailable";
            return false;
        }

        var summary = BuildSummary(state);
        var context = BuildContext(state, phase);
        var signature = BuildChoiceSignature(state, phase, summary, choices);
        var decisionId = BuildDecisionId(state, phase, signature);
        var knowledge = BuildKnowledge(state, options.include_relevant_game_data);

        decision = new DecisionWindowPayload
        {
            decision_id = decisionId,
            decision_version = DecisionVersion,
            state_version = state.state_version,
            protocol_version = ProtocolVersion,
            run_id = state.run_id,
            created_at_utc = DateTime.UtcNow.ToString("O"),
            stable = true,
            phase = phase,
            screen = state.screen,
            choice_signature = signature,
            summary = summary,
            context = context,
            choices = choices.ToArray(),
            knowledge = knowledge,
            diagnostics = options.include_raw_state ? new { raw_state = state } : null
        };
        return true;
    }

    private static string NormalizeProfile(string? profile)
    {
        var normalized = string.IsNullOrWhiteSpace(profile)
            ? DefaultProfile
            : profile.Trim().ToLowerInvariant();

        return normalized switch
        {
            "debug" => "debug",
            "full" => "full",
            _ => DefaultProfile
        };
    }

    private static string ResolvePhase(GameStatePayload state)
    {
        if (state.modal != null)
        {
            return "modal";
        }

        if (state.reward?.pending_card_choice == true)
        {
            return "reward";
        }

        return state.screen switch
        {
            "MAIN_MENU" => "main_menu",
            "CHARACTER_SELECT" => "character_select",
            "MULTIPLAYER_LOBBY" => "character_select",
            "COMBAT" => "combat",
            "CARD_SELECTION" => "combat_selection",
            "CARDS_VIEW" => "combat_selection",
            "MAP" => "map",
            "REWARD" => "reward",
            "EVENT" => "event",
            "REST" => "rest",
            "SHOP" => "shop",
            "CHEST" => "chest",
            "GAME_OVER" => "game_over",
            _ => "unknown"
        };
    }

    private static List<DecisionChoicePayload> BuildChoices(GameStatePayload state, string phase, string profile)
    {
        var choices = new List<DecisionChoicePayload>();
        var actions = new HashSet<string>(state.available_actions, StringComparer.OrdinalIgnoreCase);

        if (state.modal != null)
        {
            if (actions.Contains("confirm_modal") || state.modal.can_confirm)
            {
                choices.Add(NoArgChoice(
                    "modal:confirm",
                    "confirm_modal",
                    "Confirm modal",
                    state.modal.confirm_label,
                    "confirm_modal",
                    state.screen));
            }

            if (actions.Contains("dismiss_modal") || state.modal.can_dismiss)
            {
                choices.Add(NoArgChoice(
                    "modal:dismiss",
                    "dismiss_modal",
                    "Dismiss modal",
                    state.modal.dismiss_label,
                    "dismiss_modal",
                    state.screen));
            }

            return choices;
        }

        if (phase != "combat")
        {
            AddRunPotionChoices(choices, state, actions, phase);
        }

        switch (phase)
        {
            case "combat":
                AddCombatChoices(choices, state, actions);
                break;
            case "combat_selection":
            case "selection":
                AddSelectionChoices(choices, state, actions, phase);
                break;
            case "map":
                AddMapChoices(choices, state);
                break;
            case "reward":
                AddRewardChoices(choices, state, actions);
                break;
            case "event":
                AddEventChoices(choices, state);
                break;
            case "rest":
                AddRestChoices(choices, state, actions);
                break;
            case "shop":
                AddShopChoices(choices, state, actions);
                break;
            case "chest":
                AddChestChoices(choices, state, actions);
                break;
            case "main_menu":
                AddMainMenuChoices(choices, state, actions, profile);
                break;
            case "character_select":
                AddCharacterSelectChoices(choices, state, actions);
                break;
            case "game_over":
                if (actions.Contains("return_to_main_menu") || state.game_over?.can_return_to_main_menu == true)
                {
                    choices.Add(NoArgChoice(
                        "game_over:return_to_main_menu",
                        "return_to_main_menu",
                        "Return to main menu",
                        null,
                        "return_to_main_menu",
                        state.screen));
                }

                break;
        }

        return choices
            .Where(choice => !IsActionHiddenByProfile(choice, profile))
            .OrderBy(choice => choice.action_id, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddCombatChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions)
    {
        var combat = state.combat;
        if (combat == null)
        {
            return;
        }

        if (actions.Contains("use_potion") && state.run != null)
        {
            foreach (var potion in state.run.potions.Where(potion => potion.can_use))
            {
                if (potion.requires_target && potion.valid_target_indices.Length > 0)
                {
                    foreach (var targetIndex in potion.valid_target_indices)
                    {
                        var targetRef = $"{NormalizeTargetSpace(potion.target_index_space)}:{targetIndex}";
                        choices.Add(IndexChoice(
                            $"combat:use_potion:{potion.index}:{targetRef}",
                            "use_potion",
                            $"Use potion {potion.name ?? potion.potion_id ?? potion.index.ToString()} on {targetRef}",
                            potion.description,
                            "use_potion",
                            state.screen,
                            optionIndex: potion.index,
                            targetIndex: targetIndex,
                            sourceExtra: new Dictionary<string, object?>
                            {
                                ["potion_id"] = potion.potion_id,
                                ["target_ref"] = targetRef
                            }));
                    }
                }
                else
                {
                    choices.Add(IndexChoice(
                        $"combat:use_potion:{potion.index}",
                        "use_potion",
                        $"Use potion {potion.name ?? potion.potion_id ?? potion.index.ToString()}",
                        potion.description,
                        "use_potion",
                        state.screen,
                        optionIndex: potion.index,
                        sourceExtra: new Dictionary<string, object?>
                        {
                            ["potion_id"] = potion.potion_id
                        }));
                }
            }
        }

        if (actions.Contains("discard_potion") && state.run != null)
        {
            foreach (var potion in state.run.potions.Where(potion => potion.can_discard))
            {
                choices.Add(IndexChoice(
                    $"combat:discard_potion:{potion.index}",
                    "discard_potion",
                    $"Discard potion {potion.name ?? potion.potion_id ?? potion.index.ToString()}",
                    potion.description,
                    "discard_potion",
                    state.screen,
                    optionIndex: potion.index,
                    riskTags: new[] { "irreversible" },
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["potion_id"] = potion.potion_id
                    }));
            }
        }

        if (actions.Contains("play_card"))
        {
            foreach (var card in combat.hand.Where(card => card.playable))
            {
                if (card.requires_target && card.valid_target_indices.Length > 0)
                {
                    foreach (var targetIndex in card.valid_target_indices)
                    {
                        var targetRef = $"{NormalizeTargetSpace(card.target_index_space)}:{targetIndex}";
                        choices.Add(IndexChoice(
                            $"combat:play:{card.index}:{targetRef}",
                            "play_card",
                            $"Play {card.name} on {targetRef}",
                            BuildCardSummary(card.rules_text, card.keywords, card.mods),
                            "play_card",
                            state.screen,
                            cardIndex: card.index,
                            targetIndex: targetIndex,
                            sourceExtra: BuildPlayCardSource(combat, card, targetIndex, targetRef),
                            preview: BuildPlayCardPreview(combat, card, targetIndex)));
                    }
                }
                else
                {
                    choices.Add(IndexChoice(
                        $"combat:play:{card.index}",
                        "play_card",
                        $"Play {card.name}",
                        BuildCardSummary(card.rules_text, card.keywords, card.mods),
                        "play_card",
                        state.screen,
                        cardIndex: card.index,
                        sourceExtra: BuildPlayCardSource(combat, card, null, null),
                        preview: BuildPlayCardPreview(combat, card, null)));
                }
            }
        }

        if (actions.Contains("end_turn"))
        {
            var incomingDamage = combat.incoming_damage;
            var unblockedDamage = Math.Max(0, incomingDamage - combat.player.block);
            var riskTags = new List<string>();
            if (incomingDamage > 0)
            {
                riskTags.Add("incoming_damage");
            }

            if (unblockedDamage >= combat.player.current_hp && incomingDamage > 0)
            {
                riskTags.Add("lethal");
            }

            choices.Add(NoArgChoice(
                "combat:end_turn",
                "end_turn",
                "End turn",
                incomingDamage > 0 ? $"Incoming damage: {incomingDamage}." : null,
                "end_turn",
                state.screen,
                riskTags.ToArray(),
                new Dictionary<string, object?>
                {
                    ["incoming_damage"] = incomingDamage,
                    ["unblocked_damage"] = unblockedDamage,
                    ["player_powers"] = FormatPowers(combat.player.powers),
                    ["enemy_powers"] = combat.enemies
                        .Where(enemy => enemy.is_alive)
                        .Select(enemy => new
                        {
                            enemy.index,
                            enemy.name,
                            powers = FormatPowers(enemy.powers)
                        })
                        .ToArray()
                },
                preview: BuildEndTurnPreview(combat)));
        }
    }

    private static Dictionary<string, object?> BuildPlayCardSource(
        CombatPayload combat,
        CombatHandCardPayload card,
        int? targetIndex,
        string? targetRef)
    {
        var source = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["card_ref"] = card.card_ref,
            ["card_id"] = card.card_id,
            ["card_name"] = card.name,
            ["card_keywords"] = card.keywords,
            ["card_mods"] = card.mods,
            ["card_affliction_id"] = card.affliction_id,
            ["card_affliction_name"] = card.affliction_name,
            ["card_affliction_description"] = card.affliction_description,
            ["player_powers"] = FormatPowers(combat.player.powers)
        };

        if (targetIndex.HasValue)
        {
            source["target_ref"] = targetRef;

            if (string.Equals(card.target_index_space, "enemies", StringComparison.OrdinalIgnoreCase))
            {
                var enemy = combat.enemies.FirstOrDefault(candidate => candidate.index == targetIndex.Value);
                if (enemy != null)
                {
                    source["target_entity_ref"] = enemy.enemy_ref;
                    source["target_hp"] = enemy.current_hp;
                    source["target_block"] = enemy.block;
                    source["target_powers"] = FormatPowers(enemy.powers);
                    source["target_intent"] = enemy.intent;
                    source["target_intents"] = enemy.intents;
                }
            }
            else if (string.Equals(card.target_index_space, "players", StringComparison.OrdinalIgnoreCase))
            {
                if (targetIndex.Value >= 0 && targetIndex.Value < combat.players.Length)
                {
                    var player = combat.players[targetIndex.Value];
                    source["target_entity_ref"] = player.player_id;
                    source["target_hp"] = player.current_hp;
                    source["target_block"] = player.block;
                }
            }
        }

        return source;
    }

    private static object BuildPlayCardPreview(
        CombatPayload combat,
        CombatHandCardPayload card,
        int? targetIndex)
    {
        var notes = new List<string>();
        var isBound = IsBoundAffliction(card);
        if (!string.IsNullOrWhiteSpace(card.affliction_description))
        {
            notes.Add($"Affliction {card.affliction_name ?? card.affliction_id}: {card.affliction_description}");
        }

        if (isBound)
        {
            notes.Add("Only one Bound card can be played each turn; playing this card blocks the other Bound cards for the rest of the turn.");
        }

        var damageBase = ExtractFirstInt(DealDamageRegex, card.rules_text);
        var hitCount = ResolveHitCount(card, combat.player.energy, combat.player.stars);
        var damageTargets = ResolvePreviewTargets(combat, card, targetIndex);
        var damagePerHit = damageBase.HasValue
            ? EstimateCardDamagePerHit(card, combat.player.powers, damageBase.Value, notes)
            : (int?)null;
        var targetResults = damagePerHit.HasValue && damageTargets.Length > 0
            ? damageTargets.Select(enemy => BuildDamagePreviewForEnemy(enemy, damagePerHit.Value, hitCount)).ToArray()
            : Array.Empty<object>();
        var blockBase = ExtractFirstInt(GainBlockRegex, card.rules_text);
        var blockGain = blockBase.HasValue
            ? EstimateCardBlockGain(combat.player.powers, blockBase.Value, notes)
            : (int?)null;
        var powersApplied = ExtractAppliedPowers(card.rules_text);

        if (card.costs_x && combat.player.energy == 0)
        {
            notes.Add("Card is X-cost with current energy 0; preview assumes X=0 unless another effect modifies X.");
        }

        if (damageBase == null && blockBase == null && powersApplied.Length == 0)
        {
            notes.Add("No simple damage, block, or power amount could be parsed from card text.");
        }

        if (card.rules_text.Contains("random", StringComparison.OrdinalIgnoreCase) ||
            card.target_type.Contains("Random", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("Random targeting/effects are not deterministic in this preview.");
        }

        return new
        {
            estimate_confidence = ResolvePreviewConfidence(card, damageBase, blockBase, powersApplied, notes),
            card_id = card.card_id,
            card_name = card.name,
            card_type = card.card_type,
            energy_cost = card.energy_cost,
            star_cost = card.star_cost,
            x_value = card.costs_x ? combat.player.energy : (int?)null,
            affliction = string.IsNullOrWhiteSpace(card.affliction_id)
                ? null
                : new
                {
                    id = card.affliction_id,
                    name = card.affliction_name,
                    description = card.affliction_description,
                    amount = card.affliction_amount
                },
            shared_play_limit = isBound
                ? new
                {
                    group_id = "bound_cards",
                    max_plays_per_turn = 1,
                    affected_hand_indices = combat.hand
                        .Where(IsBoundAffliction)
                        .Select(candidate => candidate.index)
                        .ToArray()
                }
                : null,
            damage = damageBase.HasValue
                ? new
                {
                    base_per_hit = damageBase,
                    estimated_per_hit = damagePerHit,
                    hit_count = hitCount,
                    estimated_total_before_block = damagePerHit * hitCount,
                    targets = targetResults
                }
                : null,
            block = blockBase.HasValue
                ? new
                {
                    base_amount = blockBase,
                    estimated_gain = blockGain,
                    block_after = blockGain.HasValue ? combat.player.block + blockGain.Value : (int?)null
                }
                : null,
            powers_applied = powersApplied,
            notes = notes.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private static bool IsBoundAffliction(CombatHandCardPayload card)
    {
        return string.Equals(card.affliction_id, "BOUND", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(card.affliction_name, "Bound", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(card.affliction_name, "魂缚", StringComparison.Ordinal);
    }

    private static object BuildEndTurnPreview(CombatPayload combat)
    {
        var unblockedDamage = Math.Max(0, combat.incoming_damage - combat.player.block);
        return new
        {
            incoming_damage = combat.incoming_damage,
            current_block = combat.player.block,
            unblocked_damage = unblockedDamage,
            current_hp = combat.player.current_hp,
            hp_after = Math.Max(0, combat.player.current_hp - unblockedDamage),
            will_kill_player = combat.incoming_damage > 0 && unblockedDamage >= combat.player.current_hp,
            lethal_risks = combat.lethal_risks,
            enemy_intents = combat.enemies
                .Where(enemy => enemy.is_alive)
                .Select(enemy => new
                {
                    enemy.index,
                    enemy.enemy_id,
                    enemy.name,
                    enemy.intent,
                    enemy.move_id,
                    enemy.intents
                })
                .ToArray()
        };
    }

    private static CombatEnemyPayload[] ResolvePreviewTargets(
        CombatPayload combat,
        CombatHandCardPayload card,
        int? targetIndex)
    {
        if (string.Equals(card.target_index_space, "enemies", StringComparison.OrdinalIgnoreCase) && targetIndex.HasValue)
        {
            return combat.enemies
                .Where(enemy => enemy.index == targetIndex.Value && enemy.is_alive)
                .ToArray();
        }

        if (card.target_type.Contains("AllEnemies", StringComparison.OrdinalIgnoreCase))
        {
            return combat.enemies
                .Where(enemy => enemy.is_alive && enemy.is_hittable)
                .ToArray();
        }

        return Array.Empty<CombatEnemyPayload>();
    }

    private static int EstimateCardDamagePerHit(
        CombatHandCardPayload card,
        CombatPowerPayload[] playerPowers,
        int baseDamage,
        List<string> notes)
    {
        var damage = baseDamage;
        if (IsAttackCard(card))
        {
            var strength = GetPowerAmount(playerPowers, "STRENGTH", "Strength");
            if (strength != 0)
            {
                damage = Math.Max(0, damage + strength);
                notes.Add($"Applied Strength {strength:+#;-#;0} to attack damage.");
            }

            if (HasPower(playerPowers, "WEAK", "Weak"))
            {
                damage = (int)Math.Floor(damage * 0.75m);
                notes.Add("Applied Weak: attack damage is reduced by 25%.");
            }
        }

        return Math.Max(0, damage);
    }

    private static object BuildDamagePreviewForEnemy(
        CombatEnemyPayload enemy,
        int damagePerHit,
        int hitCount)
    {
        var adjustedPerHit = damagePerHit;
        var notes = new List<string>();
        if (HasPower(enemy.powers, "VULNERABLE", "Vulnerable"))
        {
            adjustedPerHit = (int)Math.Floor(adjustedPerHit * 1.5m);
            notes.Add("Applied target Vulnerable: attack damage is increased by 50%.");
        }

        var totalDamage = Math.Max(0, adjustedPerHit * Math.Max(1, hitCount));
        var blockDamage = Math.Min(enemy.block, totalDamage);
        var hpDamage = Math.Max(0, totalDamage - enemy.block);

        return new
        {
            target_index = enemy.index,
            enemy_id = enemy.enemy_id,
            name = enemy.name,
            current_hp = enemy.current_hp,
            current_block = enemy.block,
            estimated_per_hit = adjustedPerHit,
            hit_count = hitCount,
            estimated_total_damage = totalDamage,
            estimated_block_damage = blockDamage,
            estimated_hp_damage = hpDamage,
            estimated_remaining_hp = Math.Max(0, enemy.current_hp - hpDamage),
            will_kill = hpDamage >= enemy.current_hp && enemy.current_hp > 0,
            notes = notes.ToArray()
        };
    }

    private static int EstimateCardBlockGain(
        CombatPowerPayload[] playerPowers,
        int baseBlock,
        List<string> notes)
    {
        var block = baseBlock;
        var dexterity = GetPowerAmount(playerPowers, "DEXTERITY", "Dexterity");
        if (dexterity != 0)
        {
            block = Math.Max(0, block + dexterity);
            notes.Add($"Applied Dexterity {dexterity:+#;-#;0} to card Block.");
        }

        if (HasPower(playerPowers, "FRAIL", "Frail"))
        {
            block = (int)Math.Floor(block * 0.75m);
            notes.Add("Applied Frail: Block gained from cards is reduced by 25%.");
        }

        return Math.Max(0, block);
    }

    private static object[] ExtractAppliedPowers(string rulesText)
    {
        if (string.IsNullOrWhiteSpace(rulesText))
        {
            return Array.Empty<object>();
        }

        return ApplyPowerRegex.Matches(rulesText)
            .Select(match => new
            {
                power = match.Groups["power"].Value.Trim().TrimEnd('.'),
                amount = int.TryParse(match.Groups["amount"].Value, out var amount) ? amount : 0
            })
            .Where(item => item.amount > 0 && !string.IsNullOrWhiteSpace(item.power))
            .Cast<object>()
            .ToArray();
    }

    private static object[] MergePowerPreviews(params object[][] groups)
    {
        return groups
            .SelectMany(group => group)
            .GroupBy(item => ReadAnonymousString(item, "power"), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .Select(group => new
            {
                power = group.Key,
                amount = group.Sum(item => ReadAnonymousInt(item, "amount") ?? 0)
            })
            .Where(item => item.amount > 0)
            .Cast<object>()
            .ToArray();
    }

    private static object[] BuildPowersAppliedFromDynamicVars(Dictionary<string, object?> vars)
    {
        return vars
            .Where(pair => pair.Key.EndsWith("Power", StringComparison.OrdinalIgnoreCase))
            .Select(pair => new
            {
                power = pair.Key[..^"Power".Length],
                amount = ConvertToNullableInt(pair.Value) ?? 0
            })
            .Where(item => item.amount > 0)
            .Cast<object>()
            .ToArray();
    }

    private static string ReadAnonymousString(object item, string propertyName)
    {
        return ReadMemberValue(item, propertyName)?.ToString() ?? string.Empty;
    }

    private static int? ReadAnonymousInt(object item, string propertyName)
    {
        return ConvertToNullableInt(ReadMemberValue(item, propertyName));
    }

    private static int ResolveHitCount(CombatHandCardPayload card, int energy, int stars)
    {
        var match = TimesRegex.Match(card.rules_text ?? string.Empty);
        if (!match.Success)
        {
            return 1;
        }

        var value = match.Groups["amount"].Value;
        if (value.Equals("X", StringComparison.OrdinalIgnoreCase))
        {
            if (card.star_costs_x)
            {
                return Math.Max(0, stars);
            }

            return Math.Max(0, energy);
        }

        return int.TryParse(value, out var parsed) ? Math.Max(1, parsed) : 1;
    }

    private static int? ExtractFirstInt(Regex regex, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = regex.Match(text);
        return match.Success && int.TryParse(match.Groups["amount"].Value, out var amount)
            ? amount
            : null;
    }

    private static bool IsAttackCard(CombatHandCardPayload card)
    {
        return string.Equals(card.card_type, "Attack", StringComparison.OrdinalIgnoreCase) ||
            card.rules_text.Contains("Deal", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetPowerAmount(IEnumerable<CombatPowerPayload> powers, string powerId, string name)
    {
        return powers
            .Where(power =>
                string.Equals(power.power_id, powerId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(power.name, name, StringComparison.OrdinalIgnoreCase))
            .Sum(power => power.amount ?? 0);
    }

    private static bool HasPower(IEnumerable<CombatPowerPayload> powers, string powerId, string name)
    {
        return powers.Any(power =>
            (string.Equals(power.power_id, powerId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(power.name, name, StringComparison.OrdinalIgnoreCase)) &&
            (power.amount ?? 1) != 0);
    }

    private static string ResolvePreviewConfidence(
        CombatHandCardPayload card,
        int? damageBase,
        int? blockBase,
        object[] powersApplied,
        IReadOnlyCollection<string> notes)
    {
        if (damageBase.HasValue || blockBase.HasValue || powersApplied.Length > 0)
        {
            if (card.rules_text.Contains("random", StringComparison.OrdinalIgnoreCase) ||
                notes.Any(note => note.Contains("could be parsed", StringComparison.OrdinalIgnoreCase)))
            {
                return "medium";
            }

            return "medium";
        }

        return "low";
    }

    private static string? BuildCardSummary(
        string? rulesText,
        string[]? keywords,
        string[]? mods)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(rulesText))
        {
            parts.Add(rulesText);
        }

        if (keywords is { Length: > 0 })
        {
            parts.Add($"Keywords: {string.Join(", ", keywords)}");
        }

        if (mods is { Length: > 0 })
        {
            parts.Add($"Mods: {string.Join(", ", mods)}");
        }

        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    private static object[] FormatPowers(IEnumerable<CombatPowerPayload> powers)
    {
        return powers
            .Select(power => new
            {
                power.power_id,
                power.name,
                power.description,
                power.amount,
                power.is_debuff,
                power.stack_type,
                line = power.amount.HasValue
                    ? $"{power.name} {power.amount.Value}"
                    : power.name
            })
            .Cast<object>()
            .ToArray();
    }

    private static void AddSelectionChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions,
        string phase)
    {
        if (state.selection != null && actions.Contains("select_card_bundle"))
        {
            foreach (var bundle in state.selection.bundles)
            {
                var names = string.Join(", ", bundle.cards.Select(card => card.name));
                var summary = string.Join(" | ", bundle.cards.Select(card =>
                    $"{card.name}: {BuildCardSummary(card.rules_text, card.keywords, card.mods)}"));
                choices.Add(IndexChoice(
                    $"{phase}:select_card_bundle:{bundle.index}",
                    "select_card_bundle",
                    $"Select card bundle {bundle.index + 1}: {names}",
                    summary,
                    "select_card_bundle",
                    state.screen,
                    optionIndex: bundle.index,
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["bundle_index"] = bundle.index,
                        ["cards"] = bundle.cards.Select(card => new
                        {
                            card.card_id,
                            card.name,
                            card.rules_text,
                            card.keywords,
                            card.mods
                        }).ToArray()
                    }));
            }
        }

        if (state.selection != null && actions.Contains("select_deck_card"))
        {
            foreach (var card in state.selection.cards)
            {
                choices.Add(IndexChoice(
                    $"{phase}:select_deck_card:{card.index}",
                    "select_deck_card",
                    $"Select {card.name}",
                    BuildCardSummary(card.rules_text, card.keywords, card.mods),
                    "select_deck_card",
                    state.screen,
                    optionIndex: card.index,
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["card_ref"] = card.card_ref,
                        ["card_id"] = card.card_id,
                        ["card_name"] = card.name,
                        ["keywords"] = card.keywords,
                        ["mods"] = card.mods,
                        ["selectable"] = card.selectable
                    }));
            }
        }

        if (actions.Contains("confirm_selection") || state.selection?.can_confirm == true)
        {
            choices.Add(NoArgChoice(
                $"{phase}:confirm_selection",
                "confirm_selection",
                "Confirm selection",
                state.selection?.prompt,
                "confirm_selection",
                state.screen));
        }

        if (actions.Contains("close_cards_view"))
        {
            choices.Add(NoArgChoice(
                $"{phase}:close_cards_view",
                "close_cards_view",
                "Close cards view",
                null,
                "close_cards_view",
                state.screen));
        }
    }

    private static void AddRunPotionChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions,
        string phase)
    {
        if (state.run == null)
        {
            return;
        }

        var scope = string.IsNullOrWhiteSpace(phase) ? "run" : phase;

        if (actions.Contains("use_potion"))
        {
            foreach (var potion in state.run.potions.Where(potion => potion.can_use))
            {
                if (potion.requires_target && potion.valid_target_indices.Length > 0)
                {
                    foreach (var targetIndex in potion.valid_target_indices)
                    {
                        var targetRef = $"{NormalizeTargetSpace(potion.target_index_space)}:{targetIndex}";
                        choices.Add(IndexChoice(
                            $"{scope}:use_potion:{potion.index}:{targetRef}",
                            "use_potion",
                            $"Use potion {potion.name ?? potion.potion_id ?? potion.index.ToString()} on {targetRef}",
                            potion.description,
                            "use_potion",
                            state.screen,
                            optionIndex: potion.index,
                            targetIndex: targetIndex,
                            sourceExtra: new Dictionary<string, object?>
                            {
                                ["potion_id"] = potion.potion_id,
                                ["target_ref"] = targetRef,
                                ["usage"] = potion.usage
                            }));
                    }
                }
                else
                {
                    choices.Add(IndexChoice(
                        $"{scope}:use_potion:{potion.index}",
                        "use_potion",
                        $"Use potion {potion.name ?? potion.potion_id ?? potion.index.ToString()}",
                        potion.description,
                        "use_potion",
                        state.screen,
                        optionIndex: potion.index,
                        sourceExtra: new Dictionary<string, object?>
                        {
                            ["potion_id"] = potion.potion_id,
                            ["usage"] = potion.usage
                        }));
                }
            }
        }

        if (actions.Contains("discard_potion"))
        {
            var hasPotionReward = state.reward?.rewards.Any(reward =>
                string.Equals(reward.reward_type, "Potion", StringComparison.OrdinalIgnoreCase)) == true;

            foreach (var potion in state.run.potions.Where(potion => potion.can_discard))
            {
                choices.Add(IndexChoice(
                    $"{scope}:discard_potion:{potion.index}",
                    "discard_potion",
                    hasPotionReward
                        ? $"Discard potion {potion.name ?? potion.potion_id ?? potion.index.ToString()} to make room"
                        : $"Discard potion {potion.name ?? potion.potion_id ?? potion.index.ToString()}",
                    potion.description,
                    "discard_potion",
                    state.screen,
                    optionIndex: potion.index,
                    riskTags: new[] { "irreversible" },
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["potion_id"] = potion.potion_id,
                        ["opens_reward_potion_slot"] = hasPotionReward
                    }));
            }
        }
    }

    private static void AddMapChoices(List<DecisionChoicePayload> choices, GameStatePayload state)
    {
        if (state.map == null)
        {
            return;
        }

        foreach (var node in state.map.available_nodes)
        {
            choices.Add(IndexChoice(
                $"map:choose:{node.index}:{node.row}:{node.col}",
                "choose_map_node",
                $"Choose {node.node_type} node ({node.row},{node.col})",
                null,
                "choose_map_node",
                state.screen,
                optionIndex: node.index,
                riskTags: new[] { "irreversible" },
                sourceExtra: new Dictionary<string, object?>
                {
                    ["row"] = node.row,
                    ["col"] = node.col,
                    ["node_type"] = node.node_type,
                    ["state"] = node.state
                }));
        }
    }

    private static void AddRewardChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions)
    {
        var reward = state.reward;
        if (reward == null)
        {
            return;
        }

        if (actions.Contains("claim_reward"))
        {
            foreach (var option in reward.rewards.Where(option => option.claimable))
            {
                var isPotion = string.Equals(option.reward_type, "Potion", StringComparison.OrdinalIgnoreCase);
                var rewardName = string.IsNullOrWhiteSpace(option.name) ? option.description : option.name;
                var summary = string.IsNullOrWhiteSpace(option.details) ? option.description : option.details;
                if (isPotion)
                {
                    summary = $"Potion belt has an open slot; claiming this reward does not require discarding a potion. {summary}";
                }

                choices.Add(IndexChoice(
                    $"reward:claim:{option.index}",
                    "claim_reward",
                    $"Claim {option.reward_type}: {rewardName}",
                    summary,
                    "claim_reward",
                    state.screen,
                    optionIndex: option.index,
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["reward_type"] = option.reward_type,
                        ["reward_item_id"] = option.item_id,
                        ["potion_id"] = isPotion ? option.item_id : null,
                        ["potion_slot_available"] = isPotion ? true : null,
                        ["requires_potion_discard"] = isPotion ? false : null
                    }));
            }
        }

        if (actions.Contains("choose_reward_card"))
        {
            foreach (var card in reward.card_options)
            {
                choices.Add(IndexChoice(
                    $"reward:choose_card:{card.index}",
                    "choose_reward_card",
                    $"Choose reward card {card.name}",
                    BuildCardSummary(card.rules_text, card.keywords, card.mods),
                    "choose_reward_card",
                    state.screen,
                    optionIndex: card.index,
                    riskTags: new[] { "deck_thickening", "irreversible" },
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["card_id"] = card.card_id,
                        ["card_name"] = card.name,
                        ["keywords"] = card.keywords,
                        ["mods"] = card.mods
                    }));
            }
        }

        if (actions.Contains("skip_reward_cards"))
        {
            foreach (var alternative in reward.alternatives.DefaultIfEmpty(new RewardAlternativePayload
            {
                index = 0,
                label = "Skip"
            }))
            {
                choices.Add(IndexChoice(
                    $"reward:skip_cards:{alternative.index}",
                    "skip_reward_cards",
                    string.IsNullOrWhiteSpace(alternative.label) ? "Skip reward cards" : alternative.label,
                    null,
                    "skip_reward_cards",
                    state.screen,
                    optionIndex: alternative.index));
            }
        }

        if (reward.can_proceed && actions.Contains("skip_rewards_and_proceed"))
        {
            var hasRemainingRewards = reward.rewards.Any(option => option.claimable);
            choices.Add(NoArgChoice(
                "reward:proceed",
                "proceed",
                hasRemainingRewards ? "Skip remaining rewards and proceed" : "Proceed",
                hasRemainingRewards ? "Leave any remaining reward items unclaimed and return to the map." : null,
                "skip_rewards_and_proceed",
                state.screen,
                hasRemainingRewards ? new[] { "irreversible" } : null,
                new Dictionary<string, object?>
                {
                    ["skips_remaining_rewards"] = hasRemainingRewards
                }));
        }
    }

    private static void AddEventChoices(List<DecisionChoicePayload> choices, GameStatePayload state)
    {
        if (state.@event == null)
        {
            return;
        }

        foreach (var option in state.@event.options.Where(option => !option.is_locked))
        {
            var kind = option.is_proceed ? "proceed" : "choose_event_option";
            var riskTags = new List<string>();
            if (option.will_kill_player)
            {
                riskTags.Add("lethal");
            }
            else if (LooksLikeHpLoss(option))
            {
                riskTags.Add("hp_loss");
            }

            if (LooksLikeCurse(option))
            {
                riskTags.Add("curse");
            }

            if (!option.is_proceed)
            {
                riskTags.Add("irreversible");
            }

            choices.Add(IndexChoice(
                $"event:option:{option.index}",
                kind,
                string.IsNullOrWhiteSpace(option.title) ? $"Choose event option {option.index}" : option.title,
                option.description,
                "choose_event_option",
                state.screen,
                optionIndex: option.index,
                riskTags: riskTags.ToArray(),
                sourceExtra: new Dictionary<string, object?>
                {
                    ["event_id"] = state.@event.event_id,
                    ["text_key"] = option.text_key,
                    ["is_proceed"] = option.is_proceed
                }));
        }
    }

    private static void AddRestChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions)
    {
        if (state.rest == null)
        {
            return;
        }

        if (actions.Contains("choose_rest_option"))
        {
            foreach (var option in state.rest.options.Where(option => option.is_enabled))
            {
                choices.Add(IndexChoice(
                    $"rest:option:{option.index}:{option.option_id}",
                    "choose_rest_option",
                    option.title,
                    option.description,
                    "choose_rest_option",
                    state.screen,
                    optionIndex: option.index,
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["option_id"] = option.option_id
                    }));
            }
        }

        if (actions.Contains("proceed"))
        {
            choices.Add(NoArgChoice("rest:proceed", "proceed", "Proceed", null, "proceed", state.screen));
        }
    }

    private static void AddShopChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions)
    {
        var shop = state.shop;
        if (shop == null)
        {
            return;
        }

        if (actions.Contains("proceed"))
        {
            choices.Add(NoArgChoice("shop:proceed", "proceed", "Proceed", null, "proceed", state.screen));
        }

        if (actions.Contains("open_shop_inventory") || shop.can_open)
        {
            choices.Add(NoArgChoice("shop:open_inventory", "open_shop_inventory", "Open shop inventory", null, "open_shop_inventory", state.screen));
        }

        if (actions.Contains("close_shop_inventory") || shop.can_close)
        {
            choices.Add(NoArgChoice("shop:close_inventory", "close_shop_inventory", "Close shop inventory", null, "close_shop_inventory", state.screen));
        }

        if (actions.Contains("buy_card"))
        {
            foreach (var card in shop.cards.Where(card => card.is_stocked && card.enough_gold))
            {
                choices.Add(IndexChoice(
                    $"shop:buy_card:{card.index}",
                    "buy_card",
                    $"Buy {card.name} ({card.price}g)",
                    BuildCardSummary(card.rules_text, card.keywords, card.mods),
                    "buy_card",
                    state.screen,
                    optionIndex: card.index,
                    riskTags: new[] { "irreversible" },
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["card_id"] = card.card_id,
                        ["card_name"] = card.name,
                        ["keywords"] = card.keywords,
                        ["mods"] = card.mods,
                        ["price"] = card.price
                    }));
            }
        }

        if (actions.Contains("buy_relic"))
        {
            foreach (var relic in shop.relics.Where(relic => relic.is_stocked && relic.enough_gold))
            {
                choices.Add(IndexChoice(
                    $"shop:buy_relic:{relic.index}",
                    "buy_relic",
                    $"Buy {relic.name} ({relic.price}g)",
                    null,
                    "buy_relic",
                    state.screen,
                    optionIndex: relic.index,
                    riskTags: new[] { "irreversible" },
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["relic_id"] = relic.relic_id,
                        ["relic_name"] = relic.name,
                        ["price"] = relic.price
                    }));
            }
        }

        if (actions.Contains("buy_potion"))
        {
            foreach (var potion in shop.potions.Where(potion => potion.is_stocked && potion.enough_gold))
            {
                choices.Add(IndexChoice(
                    $"shop:buy_potion:{potion.index}",
                    "buy_potion",
                    $"Buy {potion.name ?? potion.potion_id ?? potion.index.ToString()} ({potion.price}g)",
                    potion.usage,
                    "buy_potion",
                    state.screen,
                    optionIndex: potion.index,
                    riskTags: new[] { "irreversible" },
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["potion_id"] = potion.potion_id,
                        ["potion_name"] = potion.name,
                        ["price"] = potion.price
                    }));
            }
        }

        if (actions.Contains("remove_card_at_shop") &&
            shop.card_removal is { available: true, enough_gold: true, used: false })
        {
            choices.Add(NoArgChoice(
                "shop:remove_card",
                "remove_card_at_shop",
                $"Remove a card ({shop.card_removal.price}g)",
                null,
                "remove_card_at_shop",
                state.screen,
                new[] { "irreversible" },
                new Dictionary<string, object?>
                {
                    ["price"] = shop.card_removal.price
                }));
        }
    }

    private static void AddChestChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions)
    {
        if (state.chest == null)
        {
            return;
        }

        if (actions.Contains("open_chest") && !state.chest.is_opened)
        {
            choices.Add(NoArgChoice("chest:open", "open_chest", "Open chest", null, "open_chest", state.screen));
        }

        foreach (var relic in state.chest.relic_options)
        {
            choices.Add(IndexChoice(
                $"chest:choose_relic:{relic.index}",
                "choose_treasure_relic",
                $"Choose {relic.name}",
                null,
                "choose_treasure_relic",
                state.screen,
                optionIndex: relic.index,
                riskTags: new[] { "irreversible" },
                sourceExtra: new Dictionary<string, object?>
                {
                    ["relic_id"] = relic.relic_id,
                    ["relic_name"] = relic.name
                }));
        }

        if (actions.Contains("proceed") && state.chest.is_opened && state.chest.relic_options.Length == 0)
        {
            choices.Add(NoArgChoice("chest:proceed", "proceed", "Proceed", null, "proceed", state.screen));
        }
    }

    private static void AddMainMenuChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions,
        string profile)
    {
        if (actions.Contains("continue_run"))
        {
            choices.Add(NoArgChoice("main_menu:continue_run", "continue_run", "Continue run", null, "continue_run", state.screen));
        }

        if (actions.Contains("open_character_select"))
        {
            choices.Add(NoArgChoice("main_menu:open_character_select", "open_character_select", "Open character select", null, "open_character_select", state.screen));
        }

        if (actions.Contains("open_timeline"))
        {
            choices.Add(NoArgChoice("main_menu:open_timeline", "open_timeline", "Open timeline", null, "open_timeline", state.screen));
        }

        if (actions.Contains("close_main_menu_submenu"))
        {
            choices.Add(NoArgChoice("main_menu:close_submenu", "close_main_menu_submenu", "Close submenu", null, "close_main_menu_submenu", state.screen));
        }

        if (actions.Contains("choose_timeline_epoch") && state.timeline != null)
        {
            foreach (var slot in state.timeline.slots.Where(slot => slot.is_actionable))
            {
                choices.Add(IndexChoice(
                    $"main_menu:choose_timeline_epoch:{slot.index}",
                    "choose_timeline_epoch",
                    $"Choose timeline epoch {slot.title}",
                    slot.state,
                    "choose_timeline_epoch",
                    state.screen,
                    optionIndex: slot.index,
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["epoch_id"] = slot.epoch_id
                    }));
            }
        }

        if (actions.Contains("confirm_timeline_overlay"))
        {
            choices.Add(NoArgChoice("main_menu:confirm_timeline_overlay", "confirm_timeline_overlay", "Confirm timeline overlay", null, "confirm_timeline_overlay", state.screen));
        }

        if (actions.Contains("abandon_run") && profile != DefaultProfile)
        {
            choices.Add(NoArgChoice(
                "main_menu:abandon_run",
                "abandon_run",
                "Abandon run",
                null,
                "abandon_run",
                state.screen,
                new[] { "irreversible", "debug_only" }));
        }
    }

    private static void AddCharacterSelectChoices(
        List<DecisionChoicePayload> choices,
        GameStatePayload state,
        HashSet<string> actions)
    {
        var characterSelect = state.character_select ?? new CharacterSelectPayload
        {
            characters = state.multiplayer_lobby?.characters ?? Array.Empty<CharacterSelectOptionPayload>(),
            can_embark = false
        };

        if (actions.Contains("select_character"))
        {
            foreach (var character in characterSelect.characters.Where(character => !character.is_locked && !character.is_selected))
            {
                choices.Add(IndexChoice(
                    $"character_select:select:{character.index}:{character.character_id}",
                    "select_character",
                    $"Select {character.name}",
                    null,
                    "select_character",
                    state.screen,
                    optionIndex: character.index,
                    sourceExtra: new Dictionary<string, object?>
                    {
                        ["character_id"] = character.character_id,
                        ["character_name"] = character.name
                    }));
            }
        }

        if (actions.Contains("close_main_menu_submenu"))
        {
            choices.Add(NoArgChoice(
                "character_select:close_main_menu_submenu",
                "close_main_menu_submenu",
                "Close character select",
                null,
                "close_main_menu_submenu",
                state.screen));
        }

        if (actions.Contains("embark") || characterSelect.can_embark)
        {
            choices.Add(NoArgChoice("character_select:embark", "embark", "Embark", null, "embark", state.screen));
        }

        if (actions.Contains("ready_multiplayer_lobby"))
        {
            choices.Add(NoArgChoice("character_select:ready", "ready_multiplayer_lobby", "Ready", null, "ready_multiplayer_lobby", state.screen));
        }

        if (actions.Contains("unready") || characterSelect.can_unready)
        {
            choices.Add(NoArgChoice("character_select:unready", "unready", "Unready", null, "unready", state.screen));
        }

        if (actions.Contains("increase_ascension") || characterSelect.can_increase_ascension)
        {
            choices.Add(NoArgChoice("character_select:increase_ascension", "increase_ascension", "Increase ascension", null, "increase_ascension", state.screen));
        }

        if (actions.Contains("decrease_ascension") || characterSelect.can_decrease_ascension)
        {
            choices.Add(NoArgChoice("character_select:decrease_ascension", "decrease_ascension", "Decrease ascension", null, "decrease_ascension", state.screen));
        }

        if (actions.Contains("host_multiplayer_lobby"))
        {
            choices.Add(NoArgChoice("character_select:host_multiplayer_lobby", "host_multiplayer_lobby", "Host multiplayer lobby", null, "host_multiplayer_lobby", state.screen));
        }

        if (actions.Contains("join_multiplayer_lobby"))
        {
            choices.Add(NoArgChoice("character_select:join_multiplayer_lobby", "join_multiplayer_lobby", "Join multiplayer lobby", null, "join_multiplayer_lobby", state.screen));
        }

        if (actions.Contains("disconnect_multiplayer_lobby"))
        {
            choices.Add(NoArgChoice("character_select:disconnect_multiplayer_lobby", "disconnect_multiplayer_lobby", "Disconnect multiplayer lobby", null, "disconnect_multiplayer_lobby", state.screen));
        }
    }

    private static DecisionChoicePayload NoArgChoice(
        string actionId,
        string kind,
        string label,
        string? summary,
        string v1Action,
        string screen,
        string[]? riskTags = null,
        Dictionary<string, object?>? sourceExtra = null,
        object? preview = null)
    {
        return BuildChoice(actionId, kind, label, summary, v1Action, screen, null, null, null, riskTags, sourceExtra, preview);
    }

    private static DecisionChoicePayload IndexChoice(
        string actionId,
        string kind,
        string label,
        string? summary,
        string v1Action,
        string screen,
        int? cardIndex = null,
        int? optionIndex = null,
        int? targetIndex = null,
        string[]? riskTags = null,
        Dictionary<string, object?>? sourceExtra = null,
        object? preview = null)
    {
        return BuildChoice(actionId, kind, label, summary, v1Action, screen, cardIndex, optionIndex, targetIndex, riskTags, sourceExtra, preview);
    }

    private static DecisionChoicePayload BuildChoice(
        string actionId,
        string kind,
        string label,
        string? summary,
        string v1Action,
        string screen,
        int? cardIndex,
        int? optionIndex,
        int? targetIndex,
        string[]? riskTags,
        Dictionary<string, object?>? sourceExtra,
        object? preview)
    {
        var source = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["screen"] = screen,
            ["v1_action"] = v1Action
        };

        if (cardIndex != null)
        {
            source["card_index"] = cardIndex.Value;
        }

        if (optionIndex != null)
        {
            source["option_index"] = optionIndex.Value;
        }

        if (targetIndex != null)
        {
            source["target_index"] = targetIndex.Value;
        }

        if (sourceExtra != null)
        {
            foreach (var (key, value) in sourceExtra)
            {
                source[key] = value;
            }
        }

        var tags = riskTags ?? Array.Empty<string>();
        return new DecisionChoicePayload
        {
            action_id = actionId,
            kind = kind,
            label = label,
            summary = summary,
            risk_tags = tags,
            attention = ResolveAttention(tags),
            source = source,
            params_schema = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["additionalProperties"] = false
            },
            preview = preview
        };
    }

    private static string ResolveAttention(string[] riskTags)
    {
        if (riskTags.Contains("lethal"))
        {
            return "danger";
        }

        if (riskTags.Any(tag => tag is "incoming_damage" or "hp_loss" or "curse" or "irreversible" or "debug_only"))
        {
            return "caution";
        }

        return "normal";
    }

    private static bool IsActionHiddenByProfile(DecisionChoicePayload choice, string profile)
    {
        if (profile != DefaultProfile)
        {
            return false;
        }

        return choice.risk_tags.Contains("auto_flow") || choice.risk_tags.Contains("debug_only");
    }

    private static ActionRequest BuildActionRequest(DecisionChoicePayload choice, DecisionActRequest request)
    {
        var source = choice.source;
        return new ActionRequest
        {
            action = GetString(source, "v1_action") ?? choice.kind,
            card_index = GetInt(source, "card_index"),
            option_index = GetInt(source, "option_index"),
            target_index = GetInt(source, "target_index"),
            client_context = new
            {
                source = "v2_decision",
                request.decision_id,
                request.action_id,
                request.client_note
            }
        };
    }

    private static string BuildDecisionId(GameStatePayload state, string phase, string signature)
    {
        var floor = state.run?.floor ?? 0;
        var turn = state.turn ?? 0;
        var shortHash = signature.StartsWith("sha256:", StringComparison.Ordinal)
            ? signature["sha256:".Length..Math.Min(signature.Length, "sha256:".Length + 12)]
            : HashText(signature)[..12];
        return $"{SanitizeIdPart(state.run_id)}:f{floor}:{state.screen.ToLowerInvariant()}:{phase}:t{turn}:{shortHash}";
    }

    private static string BuildChoiceSignature(
        GameStatePayload state,
        string phase,
        Dictionary<string, object?> summary,
        IReadOnlyList<DecisionChoicePayload> choices)
    {
        var input = JsonHelper.Serialize(new
        {
            state.run_id,
            state.screen,
            phase,
            state.turn,
            summary,
            choices = choices.Select(choice => new
            {
                choice.action_id,
                choice.kind,
                choice.label,
                choice.risk_tags,
                choice.source,
                choice.preview
            }).ToArray()
        });

        return $"sha256:{HashText(input)}";
    }

    private static string HashText(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Dictionary<string, object?> BuildSummary(GameStatePayload state)
    {
        var combat = state.combat;
        var run = state.run;

        return new Dictionary<string, object?>
        {
            ["floor"] = run?.floor,
            ["turn"] = state.turn,
            ["character_id"] = run?.character_id ?? state.character_select?.selected_character_id,
            ["ascension"] = state.character_select?.ascension,
            ["current_hp"] = combat?.player.current_hp ?? run?.current_hp,
            ["max_hp"] = combat?.player.max_hp ?? run?.max_hp,
            ["block"] = combat?.player.block,
            ["energy"] = combat?.player.energy,
            ["stars"] = combat?.player.stars,
            ["cards_played_this_turn"] = combat?.cards_played_this_turn,
            ["gold"] = run?.gold,
            ["incoming_damage"] = combat?.incoming_damage,
            ["draw_pile_count"] = combat?.piles.draw.count,
            ["discard_pile_count"] = combat?.piles.discard.count,
            ["exhaust_pile_count"] = combat?.piles.exhaust.count,
            ["draw_pile_non_attack_count"] = combat?.piles.draw.non_attack_count,
            ["draw_pile_skill_count"] = combat?.piles.draw.skill_count,
            ["draw_pile_block_card_count"] = combat?.piles.draw.block_card_count,
            ["draw_pile_defensive_out_count"] = combat?.piles.draw.defensive_out_count,
            ["draw_pile_non_attack_defensive_out_count"] = combat?.piles.draw.non_attack_defensive_out_count,
            ["draw_pile_skill_defensive_out_count"] = combat?.piles.draw.skill_defensive_out_count,
            ["player_powers"] = combat == null ? null : FormatPowers(combat.player.powers),
            ["enemy_powers"] = combat == null
                ? null
                : combat.enemies
                    .Where(enemy => enemy.is_alive)
                    .Select(enemy => new
                    {
                        enemy.index,
                        enemy.name,
                        powers = FormatPowers(enemy.powers)
                    })
                    .ToArray()
        };
    }

    private static Dictionary<string, object?> BuildContext(GameStatePayload state, string phase)
    {
        var context = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["session"] = state.session
        };

        switch (phase)
        {
            case "combat":
                context["combat"] = state.combat;
                context["run"] = BuildRunContext(state.run);
                break;
            case "combat_selection":
            case "selection":
                context["selection"] = BuildSelectionContext(state.selection, state.run);
                context["run"] = BuildRunContext(state.run);
                break;
            case "map":
                context["map"] = BuildMapContext(state.map);
                context["run"] = BuildRunContext(state.run);
                break;
            case "reward":
                context["reward"] = state.reward;
                context["run"] = BuildRunContext(state.run);
                break;
            case "event":
                context["event"] = BuildEventContext(state.@event);
                context["run"] = BuildRunContext(state.run);
                break;
            case "rest":
                context["rest"] = BuildRestContext(state.rest, state.run);
                context["run"] = BuildRunContext(state.run);
                break;
            case "shop":
                context["shop"] = BuildShopContext(state);
                context["run"] = BuildRunContext(state.run);
                break;
            case "chest":
                context["chest"] = BuildChestContext(state);
                context["run"] = BuildRunContext(state.run);
                break;
            case "modal":
                context["modal"] = state.modal;
                break;
            case "character_select":
                context["character_select"] = state.character_select;
                context["multiplayer_lobby"] = state.multiplayer_lobby;
                break;
            case "game_over":
                context["game_over"] = state.game_over;
                break;
        }

        return context;
    }

    private static object? BuildMapContext(MapPayload? map)
    {
        if (map == null)
        {
            return null;
        }

        return new
        {
            map.current_node,
            map.is_travel_enabled,
            map.is_traveling,
            map.map_generation_count,
            map.rows,
            map.cols,
            map.starting_node,
            map.boss_node,
            map.second_boss_node,
            boss_info = new
            {
                boss_node = map.boss_node,
                second_boss_node = map.second_boss_node,
                known_boss_name = (string?)null
            },
            available_nodes = map.available_nodes.Select(node => new
            {
                node.index,
                node.row,
                node.col,
                node.node_type,
                node.state,
                route_summary = BuildRouteSummary(map, node)
            }).ToArray(),
            nodes = map.nodes
        };
    }

    private static object BuildRouteSummary(MapPayload map, MapNodePayload start)
    {
        var byCoord = map.nodes.ToDictionary(node => CoordKey(node.row, node.col), StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(MapGraphNodePayload Node, int Depth)>();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nearest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var startKey = CoordKey(start.row, start.col);
        var reachesBoss = false;

        if (byCoord.TryGetValue(startKey, out var startNode))
        {
            queue.Enqueue((startNode, 0));
        }

        while (queue.Count > 0)
        {
            var (node, depth) = queue.Dequeue();
            var key = CoordKey(node.row, node.col);
            if (!visited.Add(key))
            {
                continue;
            }

            counts[node.node_type] = counts.TryGetValue(node.node_type, out var count) ? count + 1 : 1;
            if (!nearest.ContainsKey(node.node_type))
            {
                nearest[node.node_type] = depth;
            }

            if (node.is_boss || node.is_second_boss)
            {
                reachesBoss = true;
                continue;
            }

            foreach (var child in node.children)
            {
                if (byCoord.TryGetValue(CoordKey(child.row, child.col), out var childNode))
                {
                    queue.Enqueue((childNode, depth + 1));
                }
            }
        }

        return new
        {
            reaches_boss = reachesBoss,
            reachable_nodes = Math.Max(0, visited.Count),
            counts_by_type = counts,
            nearest_depth_by_type = nearest,
            first_steps = startNodeChildren(map, start)
        };

        static object[] startNodeChildren(MapPayload mapPayload, MapNodePayload node)
        {
            var graphNode = mapPayload.nodes.FirstOrDefault(candidate =>
                candidate.row == node.row && candidate.col == node.col);
            if (graphNode == null)
            {
                return Array.Empty<object>();
            }

            return graphNode.children
                .Select(child => mapPayload.nodes.FirstOrDefault(candidate =>
                    candidate.row == child.row && candidate.col == child.col))
                .Where(child => child != null)
                .Select(child => new
                {
                    child!.row,
                    child.col,
                    child.node_type
                })
                .Cast<object>()
                .ToArray();
        }
    }

    private static string CoordKey(int row, int col)
    {
        return $"{row}:{col}";
    }

    private static object? BuildEventContext(EventPayload? eventPayload)
    {
        if (eventPayload == null)
        {
            return null;
        }

        return new
        {
            eventPayload.event_id,
            eventPayload.title,
            eventPayload.description,
            eventPayload.is_finished,
            options = eventPayload.options.Select(option => new
            {
                option.index,
                option.text_key,
                option.title,
                option.description,
                option.is_locked,
                option.is_proceed,
                option.will_kill_player,
                option.has_relic_preview,
                estimated_effects = BuildTextEffectPreview($"{option.title} {option.description}", option.will_kill_player)
            }).ToArray()
        };
    }

    private static object? BuildRestContext(RestPayload? rest, RunPayload? run)
    {
        if (rest == null)
        {
            return null;
        }

        return new
        {
            current_hp = run?.current_hp,
            max_hp = run?.max_hp,
            missing_hp = run == null ? (int?)null : Math.Max(0, run.max_hp - run.current_hp),
            options = rest.options.Select(option => new
            {
                option.index,
                option.option_id,
                option.title,
                option.description,
                option.is_enabled,
                expected_values = BuildRestOptionPreview(option, run)
            }).ToArray()
        };
    }

    private static object BuildRestOptionPreview(RestOptionPayload option, RunPayload? run)
    {
        var text = $"{option.title} {option.description}";
        var heal = ExtractFirstNumberNear(text, "Heal");
        return new
        {
            heal_amount = heal,
            hp_after = heal.HasValue && run != null ? Math.Min(run.max_hp, run.current_hp + heal.Value) : (int?)null,
            upgrades_card = ContainsAny(text, "Upgrade", "Smith"),
            removes_card = ContainsAny(text, "Remove"),
            transforms_card = ContainsAny(text, "Transform"),
            disabled = !option.is_enabled
        };
    }

    private static object? BuildShopContext(GameStatePayload state)
    {
        var shop = state.shop;
        if (shop == null)
        {
            return null;
        }

        var indexes = BuildGameDataIndex(state);
        return new
        {
            shop.is_open,
            shop.can_open,
            shop.can_close,
            gold = state.run?.gold,
            cards = shop.cards.Select(card => new
            {
                card.index,
                card.category,
                card.card_id,
                card.name,
                card.upgraded,
                card.card_type,
                card.rarity,
                card.energy_cost,
                card.star_cost,
                card.costs_x,
                card.star_costs_x,
                card.rules_text,
                card.keywords,
                card.mods,
                card.price,
                card.on_sale,
                card.is_stocked,
                card.enough_gold
            }).ToArray(),
            relics = shop.relics.Select(relic => new
            {
                relic.index,
                relic.relic_id,
                relic.name,
                relic.rarity,
                relic.price,
                relic.is_stocked,
                relic.enough_gold,
                description = TryLookupString(indexes, "relics", relic.relic_id, "description")
            }).ToArray(),
            potions = shop.potions.Select(potion => new
            {
                potion.index,
                potion.potion_id,
                potion.name,
                potion.rarity,
                potion.usage,
                potion.price,
                potion.is_stocked,
                potion.enough_gold,
                description = TryLookupString(indexes, "potions", potion.potion_id, "description")
            }).ToArray(),
            card_removal = shop.card_removal == null
                ? null
                : new
                {
                    shop.card_removal.price,
                    shop.card_removal.available,
                    shop.card_removal.used,
                    shop.card_removal.enough_gold,
                    removable_cards = state.run?.deck
                        .Where(card => card.can_remove)
                        .Select(card => new
                        {
                            card.index,
                            card.card_id,
                            card.name,
                            card.upgraded,
                            card.card_type,
                            card.rarity
                        })
                        .ToArray(),
                    non_removable_cards = state.run?.deck
                        .Where(card => !card.can_remove)
                        .Select(card => new
                        {
                            card.index,
                            card.card_id,
                            card.name,
                            card.keywords,
                            card.mods
                        })
                        .ToArray()
                }
        };
    }

    private static object? BuildChestContext(GameStatePayload state)
    {
        var chest = state.chest;
        if (chest == null)
        {
            return null;
        }

        var indexes = BuildGameDataIndex(state);
        return new
        {
            chest.is_opened,
            chest.has_relic_been_claimed,
            relic_options = chest.relic_options.Select(relic => new
            {
                relic.index,
                relic.relic_id,
                relic.name,
                relic.rarity,
                description = TryLookupString(indexes, "relics", relic.relic_id, "description")
            }).ToArray()
        };
    }

    private static object BuildTextEffectPreview(string text, bool willKillPlayer)
    {
        return new
        {
            hp_loss = ExtractFirstNumberNear(text, "Lose", "HP"),
            max_hp_loss = ContainsAny(text, "Max HP") ? ExtractFirstNumberNear(text, "Lose") : null,
            gold_gain = ExtractFirstNumberNear(text, "Gain", "Gold"),
            gold_loss = ExtractFirstNumberNear(text, "Lose", "Gold"),
            adds_curse_or_affliction = ContainsAny(text, "Curse", "Affliction"),
            removes_card = ContainsAny(text, "Remove"),
            transforms_card = ContainsAny(text, "Transform"),
            upgrades_card = ContainsAny(text, "Upgrade"),
            grants_relic = ContainsAny(text, "Relic"),
            grants_potion = ContainsAny(text, "Potion"),
            will_kill_player = willKillPlayer
        };
    }

    private static int? ExtractFirstNumberNear(string text, params string[] requiredTerms)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            requiredTerms.Any(term => !text.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var match = Regex.Match(text, @"\d+");
        return match.Success && int.TryParse(match.Value, out var value) ? value : null;
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static object? BuildSelectionContext(SelectionPayload? selection, RunPayload? run)
    {
        if (selection == null)
        {
            return null;
        }

        var listedCardCounts = selection.cards
            .GroupBy(card => BuildCardIdentityKey(card.card_id, card.name, card.upgraded, card.rules_text), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var unlistedDeckCards = new List<object>();
        if (run != null)
        {
            foreach (var card in run.deck)
            {
                var key = BuildCardIdentityKey(card.card_id, card.name, card.upgraded, card.rules_text);
                if (listedCardCounts.TryGetValue(key, out var remaining) && remaining > 0)
                {
                    listedCardCounts[key] = remaining - 1;
                    continue;
                }

                unlistedDeckCards.Add(new
                {
                    card.index,
                    card.card_id,
                    card.name,
                    card.keywords,
                    card.mods,
                    card.can_remove,
                    exclusion_reason = card.can_remove
                        ? "not_listed_by_current_selection"
                        : "not_selectable_or_not_removable"
                });
            }
        }

        return new
        {
            selection.kind,
            selection.prompt,
            selection.min_select,
            selection.max_select,
            selection.selected_count,
            selection.requires_confirmation,
            selection.can_confirm,
            selection_note = "Only cards listed in selection.cards are legal choices for this selection. Do not choose cards from run.deck that are not listed here.",
            cards = selection.cards,
            unlisted_deck_cards = unlistedDeckCards.ToArray()
        };
    }

    private static string BuildCardIdentityKey(string cardId, string name, bool upgraded, string rulesText)
    {
        return string.Join(
            "\u001f",
            cardId,
            name,
            upgraded ? "1" : "0",
            rulesText);
    }

    private static object? BuildRunContext(RunPayload? run)
    {
        if (run == null)
        {
            return null;
        }

        return new
        {
            run.character_id,
            run.character_name,
            run.floor,
            run.current_hp,
            run.max_hp,
            run.gold,
            run.max_energy,
            deck_size = run.deck.Length,
            deck = run.deck.Select(card => new
            {
                card.index,
                card.card_id,
                card.name,
                card.upgraded,
                card.card_type,
                card.keywords,
                card.mods,
                card.can_remove
            }).ToArray(),
            non_removable_cards = run.deck
                .Where(card => !card.can_remove)
                .Select(card => new
                {
                    card.index,
                    card.card_id,
                    card.name,
                    card.keywords,
                    card.mods
                })
                .ToArray(),
            relics = run.relics,
            potions = run.potions
        };
    }

    private static Dictionary<string, object?> BuildKnowledge(GameStatePayload state, bool includeRelevantGameData)
    {
        return new Dictionary<string, object?>
        {
            ["metadata"] = BuildKnowledgeMetadata(),
            ["relevant"] = includeRelevantGameData
                ? BuildRelevantGameData(state)
                : new Dictionary<string, object?>()
        };
    }

    private static Dictionary<string, object?> BuildKnowledgeMetadata()
    {
        return new Dictionary<string, object?>
        {
            ["game_version"] = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "unknown",
            ["mod_version"] = ModVersion,
            ["data_source"] = "loaded_game_model",
            ["exported_at_utc"] = DateTime.UtcNow.ToString("O"),
            ["content_hash"] = null
        };
    }

    private static Dictionary<string, object?> BuildRelevantGameData(GameStatePayload state)
    {
        var indexes = BuildGameDataIndex(state);
        var requested = CollectRelevantGameDataIds(state);
        var relevant = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["glossary"] = BuildCoreGlossary()
        };

        foreach (var (collection, ids) in requested.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!indexes.TryGetValue(collection, out var collectionIndex))
            {
                continue;
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var id in ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.Ordinal))
            {
                if (TryGetIndexedItem(collectionIndex, id, out var item))
                {
                    values[id] = FilterFields(item, GetRelevantFields(collection));
                }
            }

            if (values.Count > 0)
            {
                relevant[collection] = values;
            }
        }

        return relevant;
    }

    private static Dictionary<string, HashSet<string>> CollectRelevantGameDataIds(GameStatePayload state)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        void Add(string collection, string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (!result.TryGetValue(collection, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[collection] = set;
            }

            set.Add(id);
        }

        if (state.combat != null)
        {
            foreach (var card in state.combat.hand)
            {
                Add("cards", card.card_id);
            }

            foreach (var pile in new[] { state.combat.piles.draw, state.combat.piles.discard, state.combat.piles.exhaust })
            {
                foreach (var stack in pile.stacks)
                {
                    Add("cards", stack.card_id);
                }
            }

            foreach (var enemy in state.combat.enemies)
            {
                Add("monsters", enemy.enemy_id);
                foreach (var power in enemy.powers)
                {
                    Add("powers", power.power_id);
                }
            }

            foreach (var power in state.combat.player.powers)
            {
                Add("powers", power.power_id);
            }
        }

        if (state.reward != null)
        {
            foreach (var card in state.reward.card_options)
            {
                Add("cards", card.card_id);
            }

            foreach (var option in state.reward.rewards)
            {
                if (string.Equals(option.reward_type, "Potion", StringComparison.OrdinalIgnoreCase))
                {
                    Add("potions", option.item_id);
                }
            }
        }

        if (state.selection != null)
        {
            foreach (var card in state.selection.cards)
            {
                Add("cards", card.card_id);
            }
        }

        if (state.shop != null)
        {
            foreach (var card in state.shop.cards)
            {
                Add("cards", card.card_id);
            }

            foreach (var relic in state.shop.relics)
            {
                Add("relics", relic.relic_id);
            }

            foreach (var potion in state.shop.potions)
            {
                Add("potions", potion.potion_id);
            }
        }

        if (state.chest != null)
        {
            foreach (var relic in state.chest.relic_options)
            {
                Add("relics", relic.relic_id);
            }
        }

        if (state.run != null)
        {
            foreach (var relic in state.run.relics)
            {
                Add("relics", relic.relic_id);
            }

            foreach (var potion in state.run.potions.Where(potion => potion.occupied))
            {
                Add("potions", potion.potion_id);
            }

            foreach (var card in state.run.deck.Where(card => !card.can_remove))
            {
                Add("cards", card.card_id);
            }
        }

        if (state.@event != null)
        {
            Add("events", state.@event.event_id);
        }

        return result;
    }

    private static string[] GetRelevantFields(string collection)
    {
        return collection.ToLowerInvariant() switch
        {
            "cards" => new[]
            {
                "id", "name", "description", "type", "rarity", "target", "cost", "star_cost",
                "is_x_cost", "is_x_star_cost", "damage", "block", "hit_count", "powers_applied",
                "cards_draw", "energy_gain", "hp_loss", "keywords", "tags", "can_remove"
            },
            "monsters" => new[] { "id", "name", "type", "current_hp", "max_hp", "block", "intent", "move_id", "intents", "powers", "moves", "damage_values", "block_values" },
            "powers" => new[] { "id", "name", "description", "type", "stack_type", "amount", "is_debuff" },
            "relics" => new[] { "id", "name", "description", "rarity", "stack", "is_melted", "price" },
            "potions" => new[] { "id", "name", "description", "rarity", "usage", "target", "can_use", "can_discard", "price" },
            "events" => new[] { "id", "name", "description", "options", "is_finished" },
            _ => Array.Empty<string>()
        };
    }

    private static Dictionary<string, object?> BuildCoreGlossary()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Strength"] = "Adds to Attack damage before multiplicative modifiers.",
            ["Weak"] = "Attack damage dealt is reduced by 25%.",
            ["Vulnerable"] = "Attack damage received is increased by 50%.",
            ["Dexterity"] = "Adds to Block gained from cards before multiplicative modifiers.",
            ["Frail"] = "Block gained from cards is reduced by 25%.",
            ["Block"] = "Block absorbs damage before HP loss.",
            ["Eternal"] = "Card cannot be removed from the deck."
        };
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, object?>>> BuildGameDataIndex(GameStatePayload state)
    {
        var indexes = CloneIndexes(BuildModelDbGameDataIndex());
        MergeIndexes(indexes, BuildVisibleGameDataIndex(state));
        return indexes;
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, object?>>> CloneIndexes(
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> source)
    {
        return source.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, Dictionary<string, object?>>(pair.Value, StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void MergeIndexes(
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> target,
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> source)
    {
        foreach (var (collection, sourceIndex) in source)
        {
            if (!target.TryGetValue(collection, out var targetIndex))
            {
                targetIndex = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
                target[collection] = targetIndex;
            }

            foreach (var (id, item) in sourceIndex)
            {
                targetIndex[id] = item;
            }
        }
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, object?>>> BuildModelDbGameDataIndex()
    {
        if (_modelDbGameDataIndex != null)
        {
            return _modelDbGameDataIndex;
        }

        var indexes = new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);

        AddModelCollection("cards", "AllCards");
        AddModelCollection("monsters", "Monsters");
        AddModelCollection("relics", "AllRelics");
        AddModelCollection("potions", "AllPotions");
        AddModelCollection("events", "AllEvents");
        AddModelCollection("events", "AllAncients");
        AddModelCollection("powers", "AllPowers");
        AddCorePowerData(indexes);

        _modelDbGameDataIndex = indexes;
        return _modelDbGameDataIndex;

        void AddModelCollection(string collection, string propertyName)
        {
            foreach (var model in EnumerateModelDbCollection(propertyName))
            {
                var item = BuildModelDbItem(collection, model);
                if (!item.TryGetValue("id", out var idValue))
                {
                    continue;
                }

                AddIndexedItem(indexes, collection, idValue?.ToString(), item);
            }
        }
    }

    private static IEnumerable<object> EnumerateModelDbCollection(string propertyName)
    {
        object? value;
        try
        {
            value = typeof(ModelDb).GetProperty(propertyName)?.GetValue(null);
        }
        catch
        {
            yield break;
        }

        if (value is not System.Collections.IEnumerable enumerable || value is string)
        {
            yield break;
        }

        foreach (var item in enumerable)
        {
            if (item != null)
            {
                yield return item;
            }
        }
    }

    private static Dictionary<string, object?> BuildModelDbItem(string collection, object model)
    {
        var id = ExtractModelId(model);
        var description = ReadModelText(model, "DynamicDescription", "SmartDescription", "Description", "RulesText", "Text");
        var item = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["name"] = ExtractModelName(model, id),
            ["description"] = description,
            ["model_type"] = model.GetType().Name
        };

        switch (collection.ToLowerInvariant())
        {
            case "cards":
                AddCardModelFields(item, model, description);
                break;
            case "monsters":
                item["type"] = ReadModelValue(model, "Type")?.ToString();
                item["min_hp"] = ReadModelValue(model, "MinHp");
                item["max_hp"] = ReadModelValue(model, "MaxHp");
                item["moves"] = ReadMoveSummaries(model);
                break;
            case "relics":
                item["rarity"] = ReadModelValue(model, "Rarity")?.ToString();
                break;
            case "potions":
                item["rarity"] = ReadModelValue(model, "Rarity")?.ToString();
                item["usage"] = ReadModelValue(model, "Usage")?.ToString();
                item["target"] = ReadModelValue(model, "TargetType")?.ToString();
                break;
            case "events":
                item["event_type"] = model.GetType().Name;
                break;
            case "powers":
                item["type"] = ReadModelValue(model, "Type")?.ToString();
                item["stack_type"] = ReadModelValue(model, "StackType")?.ToString();
                break;
        }

        return item;
    }

    private static void AddCardModelFields(Dictionary<string, object?> item, object model, string description)
    {
        var vars = ReadDynamicVars(model);
        item["type"] = ReadModelValue(model, "Type")?.ToString();
        item["rarity"] = ReadModelValue(model, "Rarity")?.ToString();
        item["target"] = ReadModelValue(model, "TargetType")?.ToString();
        item["cost"] = ReadEnergyCost(model);
        item["star_cost"] = ReadStarCost(model);
        item["is_x_cost"] = ReadBool(model, "HasEnergyCostX") || ReadNestedBool(model, "EnergyCost", "CostsX");
        item["is_x_star_cost"] = ReadBool(model, "HasStarCostX");
        item["damage"] = ExtractFirstInt(DealDamageRegex, description) ?? ReadDynamicVarInt(vars, "Damage");
        item["block"] = ExtractFirstInt(GainBlockRegex, description) ?? ReadDynamicVarInt(vars, "Block");
        item["hit_count"] = ExtractModelHitCount(description);
        item["powers_applied"] = MergePowerPreviews(ExtractAppliedPowers(description), BuildPowersAppliedFromDynamicVars(vars));
        item["keywords"] = ReadStringArray(model, "Keywords", "LocalKeywords");
        item["tags"] = ReadStringArray(model, "Tags");
        item["vars"] = vars.Count == 0 ? null : vars;
    }

    private static int? ReadEnergyCost(object model)
    {
        try
        {
            if (model is CardModel card)
            {
                return card.EnergyCost.GetWithModifiers(CostModifiers.All);
            }
        }
        catch
        {
        }

        return ReadNestedInt(model, "EnergyCost", "Canonical") ??
            ReadNestedInt(model, "CanonicalEnergyCost", "Canonical");
    }

    private static int? ReadStarCost(object model)
    {
        try
        {
            if (model is CardModel card)
            {
                return Math.Max(0, card.GetStarCostWithModifiers());
            }
        }
        catch
        {
        }

        return ReadInt(model, "CurrentStarCost");
    }

    private static int? ExtractModelHitCount(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var match = TimesRegex.Match(description);
        if (!match.Success || match.Groups["amount"].Value.Equals("X", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return int.TryParse(match.Groups["amount"].Value, out var amount) ? amount : null;
    }

    private static object[] ReadMoveSummaries(object model)
    {
        var value = ReadModelValue(model, "Moves") ?? ReadModelValue(model, "MoveStateMachine");
        if (value is not System.Collections.IEnumerable enumerable || value is string)
        {
            return Array.Empty<object>();
        }

        return enumerable
            .Cast<object?>()
            .Where(item => item != null)
            .Select(item =>
            {
                var id = ExtractModelId(item!);
                return new
                {
                    id,
                    name = ExtractModelName(item!, id)
                };
            })
            .Cast<object>()
            .ToArray();
    }

    private static void AddCorePowerData(Dictionary<string, Dictionary<string, Dictionary<string, object?>>> indexes)
    {
        foreach (var item in new[]
        {
            PowerItem("STRENGTH", "Strength", "Strength adds additional damage to Attacks.", "Buff"),
            PowerItem("WEAK", "Weak", "Weakened creatures deal 25% less damage with Attacks.", "Debuff"),
            PowerItem("VULNERABLE", "Vulnerable", "Vulnerable creatures take 50% more damage from Attacks.", "Debuff"),
            PowerItem("DEXTERITY", "Dexterity", "Dexterity improves Block gained from cards.", "Buff"),
            PowerItem("FRAIL", "Frail", "While Frail, gain 25% less Block from cards.", "Debuff"),
            PowerItem("FOCUS", "Focus", "Focus modifies orb values.", "Buff"),
            PowerItem("THORNS", "Thorns", "Deals damage back to attackers when hit.", "Buff")
        })
        {
            var id = item["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(id) ||
                !indexes.TryGetValue("powers", out var powerIndex) ||
                !TryGetIndexedItem(powerIndex, id, out _))
            {
                AddIndexedItem(indexes, "powers", id, item);
            }
        }

        static Dictionary<string, object?> PowerItem(string id, string name, string description, string type)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["name"] = name,
                ["description"] = description,
                ["type"] = type
            };
        }
    }

    private static Dictionary<string, Dictionary<string, Dictionary<string, object?>>> BuildVisibleGameDataIndex(GameStatePayload state)
    {
        var indexes = new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>(StringComparer.OrdinalIgnoreCase);

        void Add(string collection, string? id, Dictionary<string, object?> item)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (!indexes.TryGetValue(collection, out var collectionIndex))
            {
                collectionIndex = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
                indexes[collection] = collectionIndex;
            }

            collectionIndex[id] = item;
            collectionIndex[id.ToUpperInvariant()] = item;
            collectionIndex[id.ToLowerInvariant()] = item;
        }

        void AddIfMissing(string collection, string? id, Dictionary<string, object?> item)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            if (indexes.TryGetValue(collection, out var collectionIndex) &&
                collectionIndex.ContainsKey(id))
            {
                return;
            }

            Add(collection, id, item);
        }

        if (state.combat != null)
        {
            foreach (var card in state.combat.hand)
            {
                Add("cards", card.card_id, new Dictionary<string, object?>
                {
                    ["id"] = card.card_id,
                    ["name"] = card.name,
                    ["description"] = card.rules_text,
                    ["type"] = card.card_type,
                    ["rarity"] = card.rarity,
                    ["target"] = card.target_type,
                    ["cost"] = card.energy_cost,
                    ["star_cost"] = card.star_cost,
                    ["is_x_cost"] = card.costs_x,
                    ["is_x_star_cost"] = card.star_costs_x,
                    ["playable"] = card.playable,
                    ["unplayable_reason"] = card.unplayable_reason,
                    ["keywords"] = card.keywords,
                    ["mods"] = card.mods
                });
            }

            foreach (var pile in new[] { state.combat.piles.draw, state.combat.piles.discard, state.combat.piles.exhaust })
            {
                foreach (var stack in pile.stacks)
                {
                    AddIfMissing("cards", stack.card_id, new Dictionary<string, object?>
                    {
                        ["id"] = stack.card_id,
                        ["name"] = stack.name,
                        ["description"] = stack.rules_text,
                        ["type"] = stack.card_type,
                        ["rarity"] = stack.rarity,
                        ["cost"] = stack.energy_cost,
                        ["star_cost"] = stack.star_cost,
                        ["is_x_cost"] = stack.costs_x,
                        ["is_x_star_cost"] = stack.star_costs_x,
                        ["pile"] = pile.pile,
                        ["pile_count"] = stack.count,
                        ["non_attack"] = stack.non_attack,
                        ["block_card"] = stack.block_card,
                        ["defensive_out"] = stack.defensive_out,
                        ["keywords"] = stack.keywords,
                        ["mods"] = stack.mods
                    });
                }
            }

            foreach (var enemy in state.combat.enemies)
            {
                Add("monsters", enemy.enemy_id, new Dictionary<string, object?>
                {
                    ["id"] = enemy.enemy_id,
                    ["name"] = enemy.name,
                    ["current_hp"] = enemy.current_hp,
                    ["max_hp"] = enemy.max_hp,
                    ["block"] = enemy.block,
                    ["intent"] = enemy.intent,
                    ["move_id"] = enemy.move_id,
                    ["intents"] = enemy.intents,
                    ["powers"] = enemy.powers
                });

                foreach (var power in enemy.powers)
                {
                    AddPower(power);
                }
            }

            foreach (var power in state.combat.player.powers)
            {
                AddPower(power);
            }
        }

        if (state.run != null)
        {
            foreach (var card in state.run.deck)
            {
                Add("cards", card.card_id, new Dictionary<string, object?>
                {
                    ["id"] = card.card_id,
                    ["name"] = card.name,
                    ["description"] = card.rules_text,
                    ["type"] = card.card_type,
                    ["rarity"] = card.rarity,
                    ["cost"] = card.energy_cost,
                    ["star_cost"] = card.star_cost,
                    ["is_x_cost"] = card.costs_x,
                    ["is_x_star_cost"] = card.star_costs_x,
                    ["keywords"] = card.keywords,
                    ["mods"] = card.mods,
                    ["can_remove"] = card.can_remove
                });
            }

            foreach (var relic in state.run.relics)
            {
                Add("relics", relic.relic_id, new Dictionary<string, object?>
                {
                    ["id"] = relic.relic_id,
                    ["name"] = relic.name,
                    ["description"] = relic.description,
                    ["stack"] = relic.stack,
                    ["is_melted"] = relic.is_melted
                });
            }

            foreach (var potion in state.run.potions.Where(potion => potion.occupied))
            {
                Add("potions", potion.potion_id, new Dictionary<string, object?>
                {
                    ["id"] = potion.potion_id,
                    ["name"] = potion.name,
                    ["description"] = potion.description,
                    ["rarity"] = potion.rarity,
                    ["usage"] = potion.usage,
                    ["target"] = potion.target_type,
                    ["can_use"] = potion.can_use,
                    ["can_discard"] = potion.can_discard
                });
            }
        }

        if (state.reward != null)
        {
            foreach (var card in state.reward.card_options)
            {
                Add("cards", card.card_id, new Dictionary<string, object?>
                {
                    ["id"] = card.card_id,
                    ["name"] = card.name,
                    ["description"] = card.rules_text,
                    ["upgraded"] = card.upgraded,
                    ["keywords"] = card.keywords,
                    ["mods"] = card.mods
                });
            }

            foreach (var option in state.reward.rewards)
            {
                if (!string.Equals(option.reward_type, "Potion", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Add("potions", option.item_id, new Dictionary<string, object?>
                {
                    ["id"] = option.item_id,
                    ["name"] = option.name,
                    ["description"] = option.details,
                    ["rarity"] = option.rarity,
                    ["usage"] = option.usage,
                    ["target"] = option.target_type,
                    ["reward_claimable"] = option.claimable
                });
            }
        }

        if (state.selection != null)
        {
            foreach (var card in state.selection.cards)
            {
                Add("cards", card.card_id, new Dictionary<string, object?>
                {
                    ["id"] = card.card_id,
                    ["name"] = card.name,
                    ["description"] = card.rules_text,
                    ["type"] = card.card_type,
                    ["rarity"] = card.rarity,
                    ["cost"] = card.energy_cost,
                    ["star_cost"] = card.star_cost,
                    ["upgraded"] = card.upgraded,
                    ["keywords"] = card.keywords,
                    ["mods"] = card.mods,
                    ["selectable"] = card.selectable
                });
            }
        }

        if (state.shop != null)
        {
            foreach (var card in state.shop.cards)
            {
                Add("cards", card.card_id, new Dictionary<string, object?>
                {
                    ["id"] = card.card_id,
                    ["name"] = card.name,
                    ["description"] = card.rules_text,
                    ["type"] = card.card_type,
                    ["rarity"] = card.rarity,
                    ["cost"] = card.energy_cost,
                    ["star_cost"] = card.star_cost,
                    ["price"] = card.price,
                    ["enough_gold"] = card.enough_gold,
                    ["keywords"] = card.keywords,
                    ["mods"] = card.mods
                });
            }

            foreach (var relic in state.shop.relics)
            {
                Add("relics", relic.relic_id, new Dictionary<string, object?>
                {
                    ["id"] = relic.relic_id,
                    ["name"] = relic.name,
                    ["rarity"] = relic.rarity,
                    ["price"] = relic.price,
                    ["enough_gold"] = relic.enough_gold
                });
            }

            foreach (var potion in state.shop.potions)
            {
                Add("potions", potion.potion_id, new Dictionary<string, object?>
                {
                    ["id"] = potion.potion_id,
                    ["name"] = potion.name,
                    ["rarity"] = potion.rarity,
                    ["usage"] = potion.usage,
                    ["price"] = potion.price,
                    ["enough_gold"] = potion.enough_gold
                });
            }
        }

        if (state.chest != null)
        {
            foreach (var relic in state.chest.relic_options)
            {
                Add("relics", relic.relic_id, new Dictionary<string, object?>
                {
                    ["id"] = relic.relic_id,
                    ["name"] = relic.name,
                    ["rarity"] = relic.rarity
                });
            }
        }

        if (state.@event != null)
        {
            Add("events", state.@event.event_id, new Dictionary<string, object?>
            {
                ["id"] = state.@event.event_id,
                ["name"] = state.@event.title,
                ["description"] = state.@event.description,
                ["options"] = state.@event.options,
                ["is_finished"] = state.@event.is_finished
            });
        }

        return indexes;

        void AddPower(CombatPowerPayload power)
        {
            Add("powers", power.power_id, new Dictionary<string, object?>
            {
                ["id"] = power.power_id,
                ["name"] = power.name,
                ["description"] = power.description,
                ["amount"] = power.amount,
                ["is_debuff"] = power.is_debuff,
                ["stack_type"] = power.stack_type
            });
        }
    }

    private static void AddIndexedItem(
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> indexes,
        string collection,
        string? id,
        Dictionary<string, object?> item)
    {
        if (string.IsNullOrWhiteSpace(collection) || string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        if (!indexes.TryGetValue(collection, out var collectionIndex))
        {
            collectionIndex = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            indexes[collection] = collectionIndex;
        }

        collectionIndex[id] = item;
        collectionIndex[id.ToUpperInvariant()] = item;
        collectionIndex[id.ToLowerInvariant()] = item;
    }

    private static string? TryLookupString(
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> indexes,
        string collection,
        string? id,
        string field)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            !indexes.TryGetValue(collection, out var collectionIndex) ||
            !TryGetIndexedItem(collectionIndex, id, out var item) ||
            !item.TryGetValue(field, out var value))
        {
            return null;
        }

        return value?.ToString();
    }

    private static string ExtractModelId(object model)
    {
        foreach (var memberName in new[] { "Id", "ModelId", "MoveId" })
        {
            var value = ReadModelValue(model, memberName);
            var entry = ReadMemberValue(value, "Entry")?.ToString();
            if (!string.IsNullOrWhiteSpace(entry))
            {
                return entry;
            }

            if (value is string text && !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        var typeName = model.GetType().Name;
        return Regex.Replace(typeName, "(Model|Power)$", string.Empty);
    }

    private static string ExtractModelName(object model, string fallback)
    {
        foreach (var memberName in new[] { "Title", "Name", "DisplayName" })
        {
            var text = TryCoerceText(ReadModelValue(model, memberName));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return NormalizeRulesText(text);
            }
        }

        return fallback;
    }

    private static string ReadModelText(object model, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var text = TryCoerceText(ReadModelValue(model, memberName));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return NormalizeRulesText(text);
            }
        }

        return string.Empty;
    }

    private static object? ReadModelValue(object? model, string memberName)
    {
        if (model == null)
        {
            return null;
        }

        return ReadMemberValue(model, memberName);
    }

    private static object? ReadMemberValue(object? instance, string memberName)
    {
        if (instance == null)
        {
            return null;
        }

        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static;

        try
        {
            var type = instance is Type typeInstance ? typeInstance : instance.GetType();
            var property = type.GetProperty(memberName, flags);
            if (property != null)
            {
                return property.GetValue(instance is Type ? null : instance);
            }

            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                return field.GetValue(instance is Type ? null : instance);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string TryCoerceText(object? value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        if (value is string text)
        {
            return text;
        }

        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        // Formatting a LocString without its dynamic vars logs an error; raw text preserves its placeholders.
        try
        {
            var method = value.GetType().GetMethod("GetRawText", flags, null, Type.EmptyTypes, null);
            if (method != null && method.ReturnType == typeof(string))
            {
                var rawText = method.Invoke(value, null) as string;
                if (!string.IsNullOrEmpty(rawText))
                {
                    return rawText;
                }
            }
        }
        catch
        {
        }

        try
        {
            var method = value.GetType().GetMethod("GetFormattedText", flags, null, Type.EmptyTypes, null);
            if (method != null && method.ReturnType == typeof(string))
            {
                return method.Invoke(value, null) as string ?? string.Empty;
            }
        }
        catch
        {
        }

        try
        {
            var textProperty = value.GetType().GetProperty("Text", flags);
            if (textProperty?.PropertyType == typeof(string))
            {
                return textProperty.GetValue(value) as string ?? string.Empty;
            }
        }
        catch
        {
        }

        return value.ToString() ?? string.Empty;
    }

    private static string NormalizeRulesText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(value, @"\[(?:/?[^\]]+)\]", string.Empty);
        normalized = Regex.Replace(normalized, @"\s+", " ");
        return normalized.Trim();
    }

    private static string[] ReadStringArray(object model, params string[] memberNames)
    {
        foreach (var memberName in memberNames)
        {
            var value = ReadModelValue(model, memberName);
            if (value is System.Collections.IEnumerable enumerable && value is not string)
            {
                var values = enumerable
                    .Cast<object?>()
                    .Select(item => item?.ToString())
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .Select(text => NormalizeRulesText(text!))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(text => text, StringComparer.Ordinal)
                    .ToArray();
                if (values.Length > 0)
                {
                    return values;
                }
            }
        }

        return Array.Empty<string>();
    }

    private static Dictionary<string, object?> ReadDynamicVars(object model)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var dynamicVars = ReadModelValue(model, "DynamicVars") ??
            ReadModelValue(model, "Vars") ??
            ReadModelValue(model, "DynamicVarSet");

        CollectDynamicVars(dynamicVars, result);
        return result;
    }

    private static void CollectDynamicVars(object? value, Dictionary<string, object?> result)
    {
        if (value == null || value is string)
        {
            return;
        }

        if (value is System.Collections.IDictionary dictionary)
        {
            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                AddDynamicVar(result, entry.Key?.ToString(), CoerceDynamicVarValue(entry.Value));
            }

            return;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item == null)
                {
                    continue;
                }

                var key = ReadMemberValue(item, "Key")?.ToString() ??
                    ReadMemberValue(item, "Name")?.ToString() ??
                    ReadMemberValue(item, "Id")?.ToString();
                var rawValue = ReadMemberValue(item, "Value") ?? item;
                AddDynamicVar(result, key, CoerceDynamicVarValue(rawValue));
            }

            if (result.Count > 0)
            {
                return;
            }
        }

        foreach (var memberName in new[] { "Vars", "Values", "Items", "_vars", "_values" })
        {
            var nested = ReadMemberValue(value, memberName);
            if (nested == null || ReferenceEquals(nested, value))
            {
                continue;
            }

            CollectDynamicVars(nested, result);
            if (result.Count > 0)
            {
                return;
            }
        }
    }

    private static void AddDynamicVar(Dictionary<string, object?> result, string? key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var normalizedKey = key.Trim();
        if (normalizedKey.Contains('.', StringComparison.Ordinal))
        {
            normalizedKey = normalizedKey[(normalizedKey.LastIndexOf('.') + 1)..];
        }

        if (!result.ContainsKey(normalizedKey))
        {
            result[normalizedKey] = value;
        }
    }

    private static object? CoerceDynamicVarValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        var direct = ConvertToNullableInt(value);
        if (direct.HasValue)
        {
            return direct.Value;
        }

        foreach (var memberName in new[] { "PreviewValue", "EnchantedValue", "BaseValue", "Value", "Amount" })
        {
            var memberValue = ReadMemberValue(value, memberName);
            var numeric = ConvertToNullableInt(memberValue);
            if (numeric.HasValue)
            {
                return numeric.Value;
            }
        }

        return TryCoerceText(value);
    }

    private static int? ReadDynamicVarInt(Dictionary<string, object?> vars, string key)
    {
        if (vars.TryGetValue(key, out var value))
        {
            return ConvertToNullableInt(value);
        }

        return null;
    }

    private static int? ConvertToNullableInt(object? value)
    {
        if (value == null)
        {
            return null;
        }

        try
        {
            return value switch
            {
                int intValue => intValue,
                long longValue => (int)longValue,
                float floatValue => (int)Math.Round(floatValue),
                double doubleValue => (int)Math.Round(doubleValue),
                decimal decimalValue => (int)Math.Round(decimalValue),
                _ when int.TryParse(value.ToString(), out var parsed) => parsed,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool ReadBool(object model, string memberName)
    {
        try
        {
            var value = ReadModelValue(model, memberName);
            return value != null && Convert.ToBoolean(value);
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadNestedBool(object model, string outerMember, string innerMember)
    {
        try
        {
            var value = ReadMemberValue(ReadModelValue(model, outerMember), innerMember);
            return value != null && Convert.ToBoolean(value);
        }
        catch
        {
            return false;
        }
    }

    private static int? ReadInt(object model, string memberName)
    {
        try
        {
            var value = ReadModelValue(model, memberName);
            return value == null ? null : Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadNestedInt(object model, string outerMember, string innerMember)
    {
        try
        {
            var value = ReadMemberValue(ReadModelValue(model, outerMember), innerMember);
            return value == null ? null : Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetIndexedItem(
        Dictionary<string, Dictionary<string, object?>> index,
        string id,
        out Dictionary<string, object?> item)
    {
        if (index.TryGetValue(id, out item!) ||
            index.TryGetValue(id.ToUpperInvariant(), out item!) ||
            index.TryGetValue(id.ToLowerInvariant(), out item!))
        {
            return true;
        }

        item = new Dictionary<string, object?>();
        return false;
    }

    private static object FilterFields(Dictionary<string, object?> item, string[] fields)
    {
        if (fields.Length == 0)
        {
            return item;
        }

        return fields
            .Where(item.ContainsKey)
            .ToDictionary(field => field, field => item[field], StringComparer.Ordinal);
    }

    private static bool LooksLikeHpLoss(EventOptionPayload option)
    {
        var text = $"{option.title} {option.description}";
        return text.Contains("HP", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("Lose", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("loss", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeCurse(EventOptionPayload option)
    {
        var text = $"{option.title} {option.description}";
        return text.Contains("curse", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("affliction", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTargetSpace(string? targetIndexSpace)
    {
        if (string.IsNullOrWhiteSpace(targetIndexSpace))
        {
            return "target";
        }

        return targetIndexSpace.Contains("player", StringComparison.OrdinalIgnoreCase)
            ? "player"
            : "enemy";
    }

    private static bool IsCombatDecisionUnstable(GameStatePayload state, IReadOnlyList<DecisionChoicePayload> choices)
    {
        var combat = state.combat;
        if (combat == null)
        {
            ResetCombatStability();
            return true;
        }

        if (!HasMeaningfulChoices(choices))
        {
            return true;
        }

        if (combat.hand.Length == 0 &&
            choices.Count > 0 &&
            choices.All(choice => choice.kind == "end_turn") &&
            combat.cards_played_this_turn == 0)
        {
            return true;
        }

        return !IsCombatSnapshotStable(BuildCombatStabilitySignature(state, choices), DateTime.UtcNow);
    }

    private static bool HasMeaningfulChoices(IReadOnlyList<DecisionChoicePayload> choices)
    {
        return choices.Any(choice => !IsPassiveChoice(choice));
    }

    private static bool IsPassiveChoice(DecisionChoicePayload choice)
    {
        return choice.kind is "discard_potion" or "save_and_quit";
    }

    private static string BuildCombatStabilitySignature(GameStatePayload state, IReadOnlyList<DecisionChoicePayload> choices)
    {
        var combat = state.combat;
        return JsonHelper.Serialize(new
        {
            state.run_id,
            state.screen,
            floor = state.run?.floor,
            state.turn,
            state.available_actions,
            player = combat == null
                ? null
                : new
                {
                    combat.player.current_hp,
                    combat.player.max_hp,
                    combat.player.block,
                    combat.player.energy,
                    combat.player.stars,
                    powers = combat.player.powers.Select(PowerStabilityProjection).ToArray()
                },
            combat?.player_turn_number,
            combat?.player_turn_phase,
            combat?.cards_played_this_turn,
            combat?.attacks_played_this_turn,
            combat?.skills_played_this_turn,
            combat?.powers_played_this_turn,
            hand = combat?.hand.Select(card => new
            {
                card.index,
                card.card_id,
                card.upgraded,
                card.energy_cost,
                card.star_cost,
                card.playable,
                card.unplayable_reason,
                valid_targets = card.valid_target_indices
            }).ToArray(),
            piles = combat == null
                ? null
                : new
                {
                    draw = PileStabilityProjection(combat.piles.draw),
                    discard = PileStabilityProjection(combat.piles.discard),
                    exhaust = PileStabilityProjection(combat.piles.exhaust)
                },
            enemies = combat?.enemies.Select(enemy => new
            {
                enemy.index,
                enemy.enemy_id,
                enemy.current_hp,
                enemy.max_hp,
                enemy.block,
                enemy.is_alive,
                enemy.is_hittable,
                enemy.intent,
                enemy.move_id,
                intents = enemy.intents.Select(intent => new
                {
                    intent.index,
                    intent.intent_type,
                    intent.label,
                    intent.damage,
                    intent.hits,
                    intent.total_damage,
                    intent.status_card_count
                }).ToArray(),
                powers = enemy.powers.Select(PowerStabilityProjection).ToArray()
            }).ToArray(),
            choices = choices.Select(choice => new
            {
                choice.action_id,
                choice.kind,
                choice.risk_tags,
                choice.source,
                choice.preview
            }).ToArray()
        });
    }

    private static object PileStabilityProjection(CombatPilePayload pile)
    {
        return new
        {
            pile.count,
            pile.stack_count,
            pile.shown_stack_count,
            pile.truncated,
            pile.omitted_stack_count,
            pile.attack_count,
            pile.skill_count,
            pile.power_count,
            pile.status_count,
            pile.curse_count,
            pile.non_attack_count,
            pile.block_card_count,
            pile.defensive_out_count,
            pile.non_attack_defensive_out_count,
            pile.skill_defensive_out_count,
            stacks = pile.stacks.Select(stack => new
            {
                stack.card_id,
                stack.upgraded,
                stack.card_type,
                stack.energy_cost,
                stack.star_cost,
                stack.count
            }).ToArray()
        };
    }

    private static object PowerStabilityProjection(CombatPowerPayload power)
    {
        return new
        {
            power.index,
            power.power_id,
            power.amount,
            power.is_debuff
        };
    }

    private static bool IsCombatSnapshotStable(string signature, DateTime nowUtc)
    {
        lock (CombatStabilityGate)
        {
            if (!string.Equals(_lastCombatStabilitySignature, signature, StringComparison.Ordinal))
            {
                _lastCombatStabilitySignature = signature;
                _lastCombatStabilityChangedUtc = nowUtc;
                return false;
            }

            return nowUtc - _lastCombatStabilityChangedUtc >= CombatStableDelay;
        }
    }

    private static void ResetCombatStability()
    {
        lock (CombatStabilityGate)
        {
            _lastCombatStabilitySignature = null;
            _lastCombatStabilityChangedUtc = DateTime.MinValue;
        }
    }

    private static string? GetString(Dictionary<string, object?> source, string key)
    {
        return source.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    private static int? GetInt(Dictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value == null)
        {
            return null;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue => (int)longValue,
            double doubleValue => (int)doubleValue,
            _ when int.TryParse(value.ToString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static string SanitizeIdPart(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return builder.Length == 0 ? "run_unknown" : builder.ToString();
    }
}

internal sealed class DecisionRequestOptions
{
    public string? profile { get; init; }

    public bool include_raw_state { get; init; }

    public bool include_relevant_game_data { get; init; }
}

internal sealed class DecisionWaitRequest
{
    public int? timeout_ms { get; init; }

    public string? profile { get; init; }

    public bool include_raw_state { get; init; }

    public bool include_relevant_game_data { get; init; } = true;

    public string? after_decision_id { get; init; }
}

internal sealed class DecisionActRequest
{
    public string? decision_id { get; init; }

    public string? action_id { get; init; }

    public Dictionary<string, object?>? @params { get; init; }

    public string? client_note { get; init; }
}

internal sealed class DecisionCurrentPayload
{
    public bool available { get; init; }

    public DecisionWindowPayload? decision { get; init; }

    public string? reason { get; init; }

    public string? screen { get; init; }

    public string? last_transition { get; init; }

    public GameStatePayload? raw_state { get; init; }
}

internal sealed class DecisionWindowPayload
{
    public string decision_id { get; init; } = string.Empty;

    public int decision_version { get; init; }

    public int state_version { get; init; }

    public string protocol_version { get; init; } = DecisionWindowService.ProtocolVersion;

    public string run_id { get; init; } = "run_unknown";

    public string created_at_utc { get; init; } = string.Empty;

    public bool stable { get; init; }

    public string phase { get; init; } = "unknown";

    public string screen { get; init; } = "UNKNOWN";

    public string choice_signature { get; init; } = string.Empty;

    public Dictionary<string, object?> summary { get; init; } = new();

    public Dictionary<string, object?> context { get; init; } = new();

    public DecisionChoicePayload[] choices { get; init; } = Array.Empty<DecisionChoicePayload>();

    public Dictionary<string, object?> knowledge { get; init; } = new();

    public object? diagnostics { get; init; }
}

internal sealed class DecisionChoicePayload
{
    public string action_id { get; init; } = string.Empty;

    public string kind { get; init; } = string.Empty;

    public string label { get; init; } = string.Empty;

    public string? summary { get; init; }

    public string[] risk_tags { get; init; } = Array.Empty<string>();

    public string attention { get; init; } = "normal";

    public Dictionary<string, object?> source { get; init; } = new();

    public Dictionary<string, object?> params_schema { get; init; } = new();

    public object? preview { get; init; }
}

internal sealed class DecisionActResponsePayload
{
    public string action_id { get; init; } = string.Empty;

    public string kind { get; init; } = string.Empty;

    public string status { get; init; } = "failed";

    public bool stable { get; init; }

    public string message { get; init; } = string.Empty;

    public string previous_decision_id { get; init; } = string.Empty;

    public DecisionWindowPayload? next_decision { get; init; }
}

internal sealed class GameDataLookupRequest
{
    public GameDataLookupItemRequest[] items { get; init; } = Array.Empty<GameDataLookupItemRequest>();

    public string[]? fields { get; init; }
}

internal sealed class GameDataLookupItemRequest
{
    public string? collection { get; init; }

    public string? id { get; init; }
}

internal sealed class GameDataLookupPayload
{
    public Dictionary<string, object?> items { get; init; } = new();

    public Dictionary<string, object?> metadata { get; init; } = new();
}

internal sealed class GameDataExportPayload
{
    public Dictionary<string, Dictionary<string, object?>> collections { get; init; } = new();

    public Dictionary<string, object?> metadata { get; init; } = new();
}
