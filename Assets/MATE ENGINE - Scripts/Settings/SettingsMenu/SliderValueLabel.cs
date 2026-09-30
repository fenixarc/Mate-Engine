using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;

// Shows a slider's value after its label, e.g. "DANCE CHANGE TIME: 15 S".
// The title stays localized: the label's LocalizeStringEvent writes the translated title first,
// then this component (a later listener on the same event) appends the value.
// Code that calls SetValueWithoutNotify should use SliderValueLabel.SetWithoutNotify so the label follows.
public class SliderValueLabel : MonoBehaviour
{
    public Slider slider;
    public TMP_Text label;
    public LocalizeStringEvent localizer;
    public string valueFormat = "0";
    public string suffix = "";

    private string title;

    public static void SetWithoutNotify(Slider slider, float value)
    {
        if (slider == null) return;
        slider.SetValueWithoutNotify(value);
        var valueLabel = slider.GetComponent<SliderValueLabel>();
        if (valueLabel != null) valueLabel.Refresh();
    }

    private void Awake()
    {
        if (slider == null) slider = GetComponent<Slider>();
        if (label != null) title = label.text;
        if (localizer != null) localizer.OnUpdateString.AddListener(OnTitleChanged);
        if (slider != null) slider.onValueChanged.AddListener(OnValueChanged);
    }

    private void OnDestroy()
    {
        if (localizer != null) localizer.OnUpdateString.RemoveListener(OnTitleChanged);
        if (slider != null) slider.onValueChanged.RemoveListener(OnValueChanged);
    }

    private void OnEnable() => Refresh();

    private void OnTitleChanged(string localized)
    {
        title = localized;
        Refresh();
    }

    private void OnValueChanged(float _) => Refresh();

    public void Refresh()
    {
        if (label == null || slider == null) return;
        label.text = $"{title}: {slider.value.ToString(valueFormat)}{suffix}";
    }
}
