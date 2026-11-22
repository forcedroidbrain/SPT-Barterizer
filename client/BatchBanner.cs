using EFT.UI;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SptBarterizer.Client;

public sealed class BatchBanner : MonoBehaviour
{
    private TextMeshProUGUI? label;
    public static void Render(TraderDealScreen screen, string? message)
    {
        var panel = screen._barterSchemePanel;
        if (panel == null) return;
        var scroll = AccessTools.Field(typeof(BarterSchemePanel), "_mainPart").GetValue(panel) as ScrollRect;
        var content = scroll?.content;
        if (content == null) return;
        var existing = content.Find("Barterizer batch information");
        if (message == null) { if (existing != null) existing.gameObject.SetActive(false); return; }
        BatchBanner banner;
        if (existing == null)
        {
            // The same vertical content layout reserves OpenBarters' basket slot.
            // Never position text over native button/price labels in unknown layouts.
            if (content.GetComponent<VerticalLayoutGroup>() == null) return;
            var source = content.GetComponentInChildren<TMP_Text>(true);
            var row = new GameObject("Barterizer batch information", typeof(RectTransform), typeof(LayoutElement), typeof(TextMeshProUGUI), typeof(BatchBanner));
            row.layer = content.gameObject.layer;
            row.transform.SetParent(content, false);
            row.transform.SetAsFirstSibling();
            var layout = row.GetComponent<LayoutElement>();
            layout.minHeight = layout.preferredHeight = 34;
            layout.flexibleHeight = 0; layout.flexibleWidth = 1;
            banner = row.GetComponent<BatchBanner>();
            banner.label = row.GetComponent<TextMeshProUGUI>();
            if (source != null) { banner.label.font = source.font; banner.label.fontSharedMaterial = source.fontSharedMaterial; }
            banner.label.fontSize = banner.label.fontSizeMax = 18;
            banner.label.fontSizeMin = 12; banner.label.enableAutoSizing = true;
            banner.label.alignment = TextAlignmentOptions.Center;
            banner.label.enableWordWrapping = false;
            banner.label.overflowMode = TextOverflowModes.Truncate;
            banner.label.raycastTarget = false;
        }
        else banner = existing.GetComponent<BatchBanner>();
        banner.gameObject.SetActive(true);
        if (banner.label != null) banner.label.text = message;
        LayoutRebuilder.MarkLayoutForRebuild(content);
    }
}
