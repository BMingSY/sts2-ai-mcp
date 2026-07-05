using System.Collections;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.addons.mega_text;

namespace STS2AIMCP.Game;

internal sealed class CardSelectionSnapshot
{
    public string Kind { get; init; } = string.Empty;

    public string? Prompt { get; init; }

    public IReadOnlyList<CardModel> Cards { get; init; } = Array.Empty<CardModel>();

    public int MinSelect { get; init; } = 1;

    public int MaxSelect { get; init; } = 1;

    public int SelectedCount { get; init; }

    public bool RequiresConfirmation { get; init; }

    public bool CanConfirm { get; init; }

    public string Source { get; init; } = "unknown";

    public bool MaybeTruncated { get; init; }
}

internal readonly record struct CardSelectionSelectResult(
    bool Success,
    string? ErrorMessage,
    bool Retryable,
    int OptionCount,
    string Source);

internal static class CardSelectionAdapter
{
    public static bool TryCreate(IScreenContext? currentScreen, out CardSelectionSnapshot snapshot)
    {
        snapshot = new CardSelectionSnapshot();

        if (currentScreen is null or NCardsViewScreen)
        {
            return false;
        }

        if (GameStateService.TryGetCombatHandSelectionMetadata(
                currentScreen,
                out var combatHand,
                out var combatHandSelection) &&
            combatHand != null)
        {
            var cards = GetCombatHandSelectionOptions(combatHand)
                .Select(holder => holder.CardModel)
                .OfType<CardModel>()
                .ToArray();
            if (cards.Length == 0)
            {
                return false;
            }

            snapshot = new CardSelectionSnapshot
            {
                Kind = combatHand.CurrentMode == NPlayerHand.Mode.UpgradeSelect
                    ? "combat_hand_upgrade_select"
                    : "combat_hand_select",
                Prompt = GetPrompt(currentScreen),
                Cards = cards,
                MinSelect = combatHandSelection.MinSelect,
                MaxSelect = combatHandSelection.MaxSelect,
                SelectedCount = combatHandSelection.SelectedCount,
                RequiresConfirmation = combatHandSelection.RequiresConfirmation,
                CanConfirm = combatHandSelection.CanConfirm,
                Source = "combat_hand_model",
                MaybeTruncated = false
            };
            return true;
        }

        if (currentScreen is NCardGridSelectionScreen cardGridSelectionScreen)
        {
            var cards = GetGridSelectionCards(cardGridSelectionScreen, out var source, out var maybeTruncated);
            if (cards.Count == 0)
            {
                return false;
            }

            var prefs = TryGetPrefs(cardGridSelectionScreen);
            snapshot = new CardSelectionSnapshot
            {
                Kind = GetKind(currentScreen),
                Prompt = GetPrompt(currentScreen),
                Cards = cards,
                MinSelect = prefs?.MinSelect ?? 1,
                MaxSelect = prefs?.MaxSelect ?? 1,
                SelectedCount = GetSelectedCardCount(cardGridSelectionScreen),
                RequiresConfirmation = RequiresGridConfirmation(cardGridSelectionScreen, prefs),
                CanConfirm = HasEnabledConfirmButton(cardGridSelectionScreen),
                Source = source,
                MaybeTruncated = maybeTruncated
            };
            return true;
        }

        if (currentScreen is NChooseACardSelectionScreen chooseCardSelectionScreen)
        {
            var cards = GetChooseCardSelectionCards(chooseCardSelectionScreen, out var source, out var maybeTruncated);
            if (cards.Count == 0)
            {
                return false;
            }

            snapshot = new CardSelectionSnapshot
            {
                Kind = GetKind(currentScreen),
                Prompt = GetPrompt(currentScreen),
                Cards = cards,
                MinSelect = 1,
                MaxSelect = 1,
                SelectedCount = GetReflectedField(chooseCardSelectionScreen, "_cardSelected") is true ? 1 : 0,
                RequiresConfirmation = false,
                CanConfirm = false,
                Source = source,
                MaybeTruncated = maybeTruncated
            };
            return true;
        }

        if (currentScreen is Node rootNode)
        {
            var holders = GetVisibleCardHolders(rootNode);
            var cards = holders
                .Select(holder => holder.CardModel)
                .OfType<CardModel>()
                .ToArray();
            if (cards.Length == 0)
            {
                return false;
            }

            snapshot = new CardSelectionSnapshot
            {
                Kind = GetKind(currentScreen),
                Prompt = GetPrompt(currentScreen),
                Cards = cards,
                MinSelect = 1,
                MaxSelect = 1,
                SelectedCount = 0,
                RequiresConfirmation = HasEnabledConfirmButton(rootNode),
                CanConfirm = HasEnabledConfirmButton(rootNode),
                Source = "visible_ui_fallback",
                MaybeTruncated = true
            };
            return true;
        }

        return false;
    }

    public static IReadOnlyList<CardModel> GetCards(IScreenContext? currentScreen)
    {
        return TryCreate(currentScreen, out var selection)
            ? selection.Cards
            : Array.Empty<CardModel>();
    }

    public static IReadOnlyList<NCardHolder> GetVisibleOptions(IScreenContext? currentScreen)
    {
        if (currentScreen is null or NCardsViewScreen)
        {
            return Array.Empty<NCardHolder>();
        }

        if (currentScreen is NCardGridSelectionScreen cardGridSelectionScreen)
        {
            return GetVisibleGridCardHolders(cardGridSelectionScreen)
                .Cast<NCardHolder>()
                .ToArray();
        }

        if (currentScreen is NChooseACardSelectionScreen chooseCardSelectionScreen)
        {
            return GetVisibleCardHolders(chooseCardSelectionScreen);
        }

        if (GameStateService.TryGetCombatHandSelection(currentScreen, out var hand) && hand != null)
        {
            return GetCombatHandSelectionOptions(hand).Cast<NCardHolder>().ToArray();
        }

        return currentScreen is Node rootNode
            ? GetVisibleCardHolders(rootNode)
            : Array.Empty<NCardHolder>();
    }

    public static CardSelectionSelectResult TrySelect(IScreenContext? currentScreen, int optionIndex)
    {
        if (!TryCreate(currentScreen, out var selection))
        {
            return new CardSelectionSelectResult(
                false,
                "Action is not available in the current state.",
                false,
                0,
                "none");
        }

        if (optionIndex < 0 || optionIndex >= selection.Cards.Count)
        {
            return new CardSelectionSelectResult(
                false,
                "option_index is out of range.",
                false,
                selection.Cards.Count,
                selection.Source);
        }

        var selectedCard = selection.Cards[optionIndex];

        if (GameStateService.TryGetCombatHandSelectionMetadata(
                currentScreen,
                out var combatHand,
                out _) &&
            combatHand != null)
        {
            return TrySelectCombatHandCard(combatHand, optionIndex, selection.Source);
        }

        if (currentScreen is NCardGridSelectionScreen cardGridSelectionScreen)
        {
            return TrySelectGridCard(cardGridSelectionScreen, selectedCard, selection.Cards.Count, selection.Source);
        }

        if (currentScreen is NChooseACardSelectionScreen chooseCardSelectionScreen)
        {
            return TrySelectHolderCard(chooseCardSelectionScreen, selectedCard, selection.Cards.Count, selection.Source);
        }

        if (currentScreen is Node rootNode)
        {
            var visibleOptions = GetVisibleCardHolders(rootNode);
            if (optionIndex >= visibleOptions.Count)
            {
                return new CardSelectionSelectResult(
                    false,
                    "option_index is out of range for visible fallback selection.",
                    false,
                    visibleOptions.Count,
                    selection.Source);
            }

            visibleOptions[optionIndex].EmitSignal(NCardHolder.SignalName.Pressed, visibleOptions[optionIndex]);
            return new CardSelectionSelectResult(true, null, false, selection.Cards.Count, selection.Source);
        }

        return new CardSelectionSelectResult(
            false,
            "Card selection screen is unavailable.",
            true,
            selection.Cards.Count,
            selection.Source);
    }

    public static string? GetPrompt(IScreenContext? currentScreen)
    {
        if (currentScreen is null or NCardsViewScreen)
        {
            return null;
        }

        if (currentScreen is NCardGridSelectionScreen cardGridSelectionScreen)
        {
            return SafeReadString(() => cardGridSelectionScreen.GetNodeOrNull<MegaRichTextLabel>("%BottomLabel")?.Text);
        }

        if (currentScreen is NChooseACardSelectionScreen chooseCardSelectionScreen)
        {
            return SafeReadString(() => chooseCardSelectionScreen.GetNodeOrNull<NCommonBanner>("Banner")?.label.Text);
        }

        if (GameStateService.TryGetCombatHandSelection(currentScreen, out var hand))
        {
            return SafeReadString(() => hand!.GetNodeOrNull<MegaRichTextLabel>("%SelectionHeader")?.Text);
        }

        if (currentScreen is Node rootNode)
        {
            return SafeReadString(() =>
                rootNode.GetNodeOrNull<MegaRichTextLabel>("%BottomLabel")?.Text ??
                FindDescendants<MegaRichTextLabel>(rootNode)
                    .FirstOrDefault(label => label.IsVisibleInTree() && !string.IsNullOrWhiteSpace(label.Text))?.Text);
        }

        return null;
    }

    private static string GetKind(IScreenContext? currentScreen)
    {
        return currentScreen switch
        {
            NDeckUpgradeSelectScreen => "deck_upgrade_select",
            NDeckTransformSelectScreen => "deck_transform_select",
            NDeckEnchantSelectScreen => "deck_enchant_select",
            NDeckCardSelectScreen => "deck_card_select",
            NCombatPileCardSelectScreen => "combat_pile_card_select",
            NSimpleCardSelectScreen => "simple_card_select",
            NChooseACardSelectionScreen => "choose_card_select",
            _ => "deck_card_select"
        };
    }

    private static IReadOnlyList<NHandCardHolder> GetCombatHandSelectionOptions(NPlayerHand hand)
    {
        return hand.ActiveHolders
            .Where(node => GodotObject.IsInstanceValid(node) && node.Visible && node.CardModel != null)
            .OrderBy(node => node.GetIndex())
            .ToArray();
    }

    private static CardSelectionSelectResult TrySelectCombatHandCard(NPlayerHand hand, int optionIndex, string source)
    {
        var options = GetCombatHandSelectionOptions(hand);
        if (optionIndex >= options.Count)
        {
            return new CardSelectionSelectResult(
                false,
                "option_index is out of range for combat hand selection.",
                false,
                options.Count,
                source);
        }

        var selected = options[optionIndex];
        hand.Call(
            hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect
                ? NPlayerHand.MethodName.SelectCardInUpgradeMode
                : NPlayerHand.MethodName.SelectCardInSimpleMode,
            selected);
        hand.Call(NPlayerHand.MethodName.CheckIfSelectionComplete);
        return new CardSelectionSelectResult(true, null, false, options.Count, source);
    }

    private static IReadOnlyList<CardModel> GetGridSelectionCards(
        NCardGridSelectionScreen screen,
        out string source,
        out bool maybeTruncated)
    {
        if (GetReflectedField(screen, "_grid") is NCardGrid grid)
        {
            var gridCards = ReadCardModels(GetReflectedField(grid, "_cards"));
            if (gridCards.Count > 0)
            {
                source = "grid_model";
                maybeTruncated = false;
                return gridCards;
            }
        }

        var screenCards = ReadCardModels(GetReflectedField(screen, "_cards"));
        if (screenCards.Count > 0)
        {
            source = "screen_model";
            maybeTruncated = false;
            return screenCards;
        }

        source = "visible_ui_fallback";
        maybeTruncated = true;
        return GetVisibleGridCardHolders(screen)
            .Select(holder => holder.CardModel)
            .OfType<CardModel>()
            .ToArray();
    }

    private static IReadOnlyList<CardModel> GetChooseCardSelectionCards(
        NChooseACardSelectionScreen screen,
        out string source,
        out bool maybeTruncated)
    {
        var screenCards = ReadCardModels(GetReflectedField(screen, "_cards"));
        if (screenCards.Count > 0)
        {
            source = "screen_model";
            maybeTruncated = false;
            return screenCards;
        }

        source = "holder_model_fallback";
        maybeTruncated = false;
        return GetAllCardHolders(screen)
            .Select(holder => holder.CardModel)
            .OfType<CardModel>()
            .ToArray();
    }

    private static CardSelectionSelectResult TrySelectGridCard(
        NCardGridSelectionScreen screen,
        CardModel card,
        int optionCount,
        string source)
    {
        try
        {
            for (var type = screen.GetType(); type != null; type = type.BaseType)
            {
                var method = type.GetMethod(
                    "OnCardClicked",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    binder: null,
                    types: new[] { typeof(CardModel) },
                    modifiers: null);
                if (method == null)
                {
                    continue;
                }

                method.Invoke(screen, new object[] { card });
                return new CardSelectionSelectResult(true, null, false, optionCount, source);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[STS2AIMCP] Failed to select grid card {card.Id.Entry}: {ex.Message}");
            return new CardSelectionSelectResult(
                false,
                "Card grid selection is unavailable.",
                true,
                optionCount,
                source);
        }

        return new CardSelectionSelectResult(
            false,
            "Card grid selection is unavailable.",
            true,
            optionCount,
            source);
    }

    private static CardSelectionSelectResult TrySelectHolderCard(
        Node screen,
        CardModel card,
        int optionCount,
        string source)
    {
        var holder = GetAllCardHolders(screen)
            .FirstOrDefault(option => ReferenceEquals(option.CardModel, card));
        if (holder == null)
        {
            return new CardSelectionSelectResult(
                false,
                "Card holder for selected card is unavailable.",
                true,
                optionCount,
                source);
        }

        holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
        return new CardSelectionSelectResult(true, null, false, optionCount, source);
    }

    private static bool RequiresGridConfirmation(NCardGridSelectionScreen screen, CardSelectorPrefs? prefs)
    {
        return screen is NDeckCardSelectScreen or NDeckUpgradeSelectScreen or NDeckTransformSelectScreen or NDeckEnchantSelectScreen ||
            prefs?.RequireManualConfirmation == true;
    }

    private static int GetSelectedCardCount(object screen)
    {
        return GetReflectedField(screen, "_selectedCards") is IEnumerable selectedCards
            ? selectedCards.Cast<object>().Count()
            : 0;
    }

    private static CardSelectorPrefs? TryGetPrefs(object screen)
    {
        return GetReflectedField(screen, "_prefs") is CardSelectorPrefs prefs
            ? prefs
            : null;
    }

    private static IReadOnlyList<CardModel> ReadCardModels(object? value)
    {
        if (value is not IEnumerable cards)
        {
            return Array.Empty<CardModel>();
        }

        var result = new List<CardModel>();
        foreach (var card in cards)
        {
            if (card is CardModel cardModel)
            {
                result.Add(cardModel);
            }
        }

        return result;
    }

    private static IReadOnlyList<NGridCardHolder> GetVisibleGridCardHolders(Node root)
    {
        return FindDescendants<NGridCardHolder>(root)
            .Where(node => GodotObject.IsInstanceValid(node) && node.IsVisibleInTree() && node.CardModel != null)
            .OrderBy(node => node.GlobalPosition.Y)
            .ThenBy(node => node.GlobalPosition.X)
            .ToArray();
    }

    private static IReadOnlyList<NCardHolder> GetVisibleCardHolders(Node root)
    {
        return GetAllCardHolders(root)
            .Where(node => node.IsVisibleInTree())
            .OrderBy(node => node.GlobalPosition.Y)
            .ThenBy(node => node.GlobalPosition.X)
            .ToArray();
    }

    private static IReadOnlyList<NCardHolder> GetAllCardHolders(Node root)
    {
        return FindDescendants<NCardHolder>(root)
            .Where(node => GodotObject.IsInstanceValid(node) && node.CardModel != null)
            .OrderBy(node => node.GlobalPosition.Y)
            .ThenBy(node => node.GlobalPosition.X)
            .ToArray();
    }

    private static bool HasEnabledConfirmButton(Node root)
    {
        return FindDescendants<NConfirmButton>(root)
            .Any(button => GodotObject.IsInstanceValid(button) && button.Visible && button.IsVisibleInTree() && button.IsEnabled);
    }

    private static List<T> FindDescendants<T>(Node root) where T : Node
    {
        var found = new List<T>();
        FindDescendantsRecursive(root, found);
        return found;
    }

    private static void FindDescendantsRecursive<T>(Node node, List<T> found) where T : Node
    {
        if (!GodotObject.IsInstanceValid(node))
        {
            return;
        }

        if (node is T typedNode)
        {
            found.Add(typedNode);
        }

        foreach (Node child in node.GetChildren())
        {
            FindDescendantsRecursive(child, found);
        }
    }

    private static object? GetReflectedField(object target, string fieldName)
    {
        try
        {
            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    return field.GetValue(target);
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? SafeReadString(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return null;
        }
    }
}
