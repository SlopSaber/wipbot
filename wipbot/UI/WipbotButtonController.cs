using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using IPA.Utilities.Async;
using IPA.Utilities;
using System;
using System.Linq;
using System.Text;
using UnityEngine;
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
            var title = BeatSaberUI.DiContainer.Resolve<HMUI.HierarchyManager>().GetField<HMUI.ScreenSystem, HMUI.HierarchyManager>("_screenSystem").titleViewController;
            BsmlParser.Parse(
                "<bg id='wipbot-root' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:schemaLocation='https://monkeymanboy.github.io/BSML-Docs/ https://raw.githubusercontent.com/monkeymanboy/BSML-Docs/gh-pages/BSMLSchema.xsd'>" +
                "<button id='wipbot-button' active='~FakeButtonActive' text='wip' font-size='3.5' on-click='wipbot-click' anchor-pos-x='" + Config.ButtonPositionX + "' anchor-pos-y='" + Config.ButtonPositionY + "' pref-height='7' pref-width='7' />" +
                "<action-button id='wipbot-button2' active='~WipButtonActive' text='~WipButtonText' hover-hint='~WipButtonHint' word-wrapping='false' font-size='3.5' on-click='wipbot-click2' anchor-pos-x='" + Config.ButtonPositionX + "' anchor-pos-y='" + Config.ButtonPositionY + "' pref-height='7' pref-width='10.5' />" +
                "</bg>"
                , title.gameObject, this);
            var levelSelection = Resources.FindObjectsOfTypeAll<LevelSelectionNavigationController>().First();
            var wipRoot = wipbotRootTransform.gameObject;
            title.gameObject.AddComponent<WipTitleButtonVisibility>().Initialize(wipRoot, levelSelection.gameObject);
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

    internal sealed class WipTitleButtonVisibility : MonoBehaviour
    {
        private GameObject buttonRoot;
        private GameObject levelSelection;
        private bool wasVisible;

        internal void Initialize(GameObject root, GameObject menu)
        {
            buttonRoot = root;
            levelSelection = menu;
            UpdateVisibility();
        }

        private void Update()
        {
            UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            if (buttonRoot == null) return;
            var shouldShow = levelSelection != null && levelSelection.activeInHierarchy;
            if (buttonRoot.activeSelf != shouldShow) buttonRoot.SetActive(shouldShow);
            if (shouldShow && !wasVisible)
            {
                buttonRoot.transform.SetAsLastSibling();
                Debug.Log("[wipbot] WIP title button visible");
            }
            wasVisible = shouldShow;
        }
    }
}
