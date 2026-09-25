using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using IPA.Utilities.Async;
using System;
using System.Linq;
using System.Text;
using UnityEngine;
using VRUIControls;
using wipbot.Models;
using Zenject;

#pragma warning disable IDE0051 // Remove unused private members
namespace wipbot.UI
{
    public class WipbotButtonController : INotifyPropertyChanged, IInitializable
    {
        [Inject] private WBConfig Config { get; set; }
        [Inject] private BSMLParser BsmlParser { get; set; }
        public event PropertyChangedEventHandler PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        internal event Action OnWipButtonPressed;
        private bool _fakeButtonActive = true;
        private bool _wipButtonActive = false;
        private string _wipButtonText = "wip";
        private string _wipButtonHint = "";

        [UIComponent("wipbot-button")]
        private RectTransform wipbotButtonTransform { get; set; }

        [UIComponent("wipbot-root")]
        private RectTransform wipbotRootTransform { get; set; }

        [UIValue("FakeButtonActive")]
        public bool FakeButtonActive
        {
            get => _fakeButtonActive;
            set { _fakeButtonActive = value; UnityMainThreadTaskScheduler.Factory.StartNew(() => NotifyPropertyChanged()); }
        }

        [UIValue("WipButtonActive")]
        public bool WipButtonActive
        {
            get => _wipButtonActive;
            set { _wipButtonActive = value; UnityMainThreadTaskScheduler.Factory.StartNew(() => NotifyPropertyChanged()); }
        }

        [UIValue("WipButtonText")]
        public string WipButtonText
        {
            get => _wipButtonText;
            set { _wipButtonText = value; UnityMainThreadTaskScheduler.Factory.StartNew(() => NotifyPropertyChanged()); }
        }

        [UIValue("WipButtonHint")]
        public string WipButtonHint
        {
            get => _wipButtonHint;
            set { _wipButtonHint = value; UnityMainThreadTaskScheduler.Factory.StartNew(() => NotifyPropertyChanged()); }
        }

        public void Initialize()
        {
            if (wipbotButtonTransform != null) return;
            BsmlParser.Parse(
                "<bg id='wipbot-root' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:schemaLocation='https://monkeymanboy.github.io/BSML-Docs/ https://raw.githubusercontent.com/monkeymanboy/BSML-Docs/gh-pages/BSMLSchema.xsd'>" +
                "<button id='wipbot-button' active='~FakeButtonActive' text='wip' font-size='3' on-click='wipbot-click' anchor-pos-x='" + Config.ButtonPositionX + "' anchor-pos-y='" + Config.ButtonPositionY + "' pref-height='6' pref-width='11' />" +
                "<action-button id='wipbot-button2' active='~WipButtonActive' text='~WipButtonText' hover-hint='~WipButtonHint' word-wrapping='false' font-size='3' on-click='wipbot-click2' anchor-pos-x='" + Config.ButtonPositionX + "' anchor-pos-y='" + Config.ButtonPositionY + "' pref-height='6' pref-width='11' />" +
                "</bg>"
                , Resources.FindObjectsOfTypeAll<LevelSelectionNavigationController>().First().gameObject, this);
            MakeMenuBarButtonInteractive(wipbotRootTransform);
        }

        private static void MakeMenuBarButtonInteractive(RectTransform root)
        {
            var parentCanvas = root.GetComponentInParent<Canvas>();
            var parentCurve = parentCanvas?.rootCanvas.GetComponent<HMUI.CurvedCanvasSettings>();
            var canvas = root.gameObject.AddComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 100;
            canvas.additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord2;
            if (parentCurve != null)
            {
                root.gameObject.AddComponent<HMUI.CurvedCanvasSettings>().SetRadius(parentCurve.radius);
            }
            if (BeatSaberUI.DiContainer.IsInstalling)
            {
                BeatSaberUI.DiContainer.QueueForInject(root.gameObject.AddComponent<VRGraphicRaycaster>());
            }
            else
            {
                BeatSaberUI.DiContainer.InstantiateComponent<VRGraphicRaycaster>(root.gameObject);
            }
        }

        [UIAction("wipbot-click2")]
        void DownloadButtonPressed()
        {
            OnWipButtonPressed?.Invoke();
        }

        internal void UpdateButtonState(QueueItem[] queueState)
        {
            WipButtonText = "wip(" + queueState.Length + ")";
            FakeButtonActive = queueState.Length == 0;
            WipButtonActive = queueState.Length > 0;

            var stringBuilder = new StringBuilder();

            for (int i = 0; i < queueState.Length; i++)
            {
                stringBuilder.Append(i + 1);
                stringBuilder.Append(": ");
                stringBuilder.Append(queueState[i].UserName);
                stringBuilder.Append("; ");
            }

            WipButtonHint = stringBuilder.ToString();
        }

        [UIAction("wipbot-click")]
        void Asdf2() { }
    }
}
