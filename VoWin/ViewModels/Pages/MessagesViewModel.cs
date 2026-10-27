using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoSharp.Common.Utils;
using VoSharp.Euicc.Models;
using VoSharp.Kernel.Pool;
using VoWin.Helpers;
using VoWin.Models;
using VoWin.Services;
using Wpf.Ui.Controls;

namespace VoWin.ViewModels.Pages
{
    public partial class MessagesViewModel : ObservableObject
    {
        private readonly IVoKernelService _kernelService;

        public MessagesViewModel ViewModel => this;
        public ObservableCollection<SmsConversationModel> Conversations => _kernelService.Conversations;
        public ICollectionView ConversationsView { get; }
        public ObservableCollection<ModemSlot> Slots => _kernelService.Slots;

        /// <summary>当前卡片的自动显示名（手填昵称优先）。</summary>
        public string CurrentCardDisplayName =>
            CardDisplayNameResolver.ResolveEffectiveName(
                SelectedSlot?.CardNickname,
                SelectedSlot?.AutoDisplayName,
                SelectedSlot?.Sim,
                SelectedSlot?.VoWifiDiag?.Ims?.PAssociatedUri)
            ?? "--";

        /// <summary>当前卡片的订阅号码（AT+CNUM 或 IMS P-Associated-URI）。</summary>
        public string CurrentCardPhoneNumber
        {
            get
            {
                var number = CardDisplayNameResolver.ResolvePhoneNumber(
                    SelectedSlot?.Sim,
                    SelectedSlot?.VoWifiDiag?.Ims?.PAssociatedUri);
                return string.IsNullOrWhiteSpace(number) ? "号码未提供" : number!;
            }
        }

        /// <summary>当前卡片的「国家 · 运营商」标签。</summary>
        public string CurrentCardCountryOperator =>
            CarrierDisplayHelper.GetCountryOperatorDisplay(SelectedSlot?.Sim);

        /// <summary>当前卡片的 IMSI（含归属 PLMN）。</summary>
        public string CurrentCardImsi =>
            string.IsNullOrWhiteSpace(SelectedSlot?.Sim?.Imsi) ? "--" : SelectedSlot!.Sim!.Imsi;

        /// <summary>当前卡片的 ICCID。</summary>
        public string CurrentCardIccid =>
            string.IsNullOrWhiteSpace(SelectedSlot?.Sim?.Iccid) ? "--" : SelectedSlot!.Sim!.Iccid;

        /// <summary>当前卡片的 VoWiFi 连接状态（用户语言，不暴露协议枚举）。</summary>
        public string CurrentCardVoWifiState => VoWifiStatePresenter.Present(
            VoWifiStatePresenter.ResolvePhase(
                SelectedSlot?.VoWifiDiag?.State ?? VoSharp.Telephony.VoWifi.VoWifiState.Disconnected,
                anySlotPresent: SelectedSlot != null,
                simReady: !string.IsNullOrWhiteSpace(SelectedSlot?.Sim?.Iccid),
                userStopped: false)).Title;

        /// <summary>eSIM Profile 列表（普通 SIM 卡片为空）。</summary>
        public ObservableCollection<Profile> EuiccProfiles { get; } = new();

        /// <summary>当前卡槽是否支持 eSIM。</summary>
        public bool HasEuiccProfiles => EuiccProfiles.Count > 0;

        [ObservableProperty]
        private SmsConversationModel? _selectedConversation;

        [ObservableProperty]
        private ModemSlot? _selectedSlot;

        [ObservableProperty]
        private string _searchQuery = string.Empty;

        [ObservableProperty]
        private string _messageText = string.Empty;

        [ObservableProperty]
        private bool _isCreatingNewChat;

        [ObservableProperty]
        private string _newRecipientInput = string.Empty;

        [ObservableProperty]
        private bool _requestDeliveryReport = true;

        [ObservableProperty]
        private bool _forceCellular;

        [ObservableProperty]
        private bool _isSending;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private bool _isCalling;

        public bool IsBusy => IsSending || IsCalling;

        public string CharCountInfo
        {
            get
            {
                int len = MessageText?.Length ?? 0;
                bool isUcs2 = MessageText != null && MessageText.Any(c => c > 127);
                int segmentSize = isUcs2 ? 70 : 160;
                if (len == 0)
                {
                    return $"0 / {segmentSize} (1条, {(isUcs2 ? "UCS-2" : "GSM-7")})";
                }

                int segments = (int)Math.Ceiling((double)len / segmentSize);
                if (segments == 0) segments = 1;
                string enc = isUcs2 ? "UCS-2" : "GSM-7";
                return $"{len} / {segmentSize * segments} ({segments}条, {enc})";
            }
        }

        public string MessageInput
        {
            get => MessageText;
            set => MessageText = value;
        }

        public string MessageTextInput
        {
            get => MessageText;
            set => MessageText = value;
        }

        public string CharacterCountInfo => CharCountInfo;

        public MessagesViewModel(IVoKernelService kernelService)
        {
            _kernelService = kernelService;
            SelectedSlot = _kernelService.ActiveSlot ?? Slots.FirstOrDefault();
            ConversationsView = CollectionViewSource.GetDefaultView(Conversations);
            ConversationsView.Filter = FilterConversation;
            Slots.CollectionChanged += (s, e) =>
            {
                if (SelectedSlot == null && Slots.Count > 0)
                {
                    SelectedSlot = Slots.FirstOrDefault();
                }
            };

            if (Conversations.Count > 0)
            {
                SelectedConversation = Conversations.First();
                SelectedConversation.UnreadCount = 0;
            }

            // 非界面发起的探测（托盘热键切卡、切卡后 SIM REFRESH）也必须
            // 让左栏的 Profile 列表同步，否则界面会一直停留在旧卡片。
            _kernelService.EuiccProbeCompleted += OnEuiccProbeCompleted;

            // 连接状态/信号/SIM 变化都要刷新左栏卡片信息，否则 VoWiFi 状态
            // 永远停留在进入页面时的快照（与首页 Dashboard 保持一致的行为）。
            void NotifyCardPanel()
            {
                RefreshLocalNumberAnnotations();
                OnPropertyChanged(string.Empty);
            }

            _kernelService.Kernel.TelephonyStateChanged += (_, _) => NotifyCardPanel();
            _kernelService.Kernel.VoWifiStateChanged += (_, _) => NotifyCardPanel();
            _kernelService.Kernel.SimStateChanged += (_, _) => NotifyCardPanel();
            _kernelService.Kernel.SignalQualityChanged += (_, _) => NotifyCardPanel();
        }

        /// <summary>
        /// 逐条补齐消息气泡的“本机号码”标注：
        /// 优先按消息所属卡槽解析该卡的号码，其次回退到当前卡片号码。
        /// 仅改动内存标注，不写库。
        /// </summary>
        private void RefreshLocalNumberAnnotations()
        {
            foreach (var conv in Conversations)
            {
                foreach (var m in conv.Messages)
                {
                    if (string.IsNullOrWhiteSpace(m.LocalPhoneNumber))
                        m.LocalPhoneNumber = ResolveLocalNumberFor(m);
                }
            }
        }

        private string? ResolveLocalNumberFor(SmsMessageModel msg)
        {
            // 只在该条短信可明确归属到某个卡槽、且能拿到该槽已核实号码时才标注；
            // 无法可靠归属时返回 null，界面不写标注（避免张冠李戴）。
            if (string.IsNullOrWhiteSpace(msg.SlotId)) return null;
            var slot = Slots.FirstOrDefault(x =>
                string.Equals(x.Id, msg.SlotId, StringComparison.OrdinalIgnoreCase));
            if (slot == null) return null;

            var live = slot.Sim?.PhoneNumber;
            if (!string.IsNullOrWhiteSpace(live)) return live!.Trim();

            var iccid = slot.Sim?.Iccid;
            if (!string.IsNullOrWhiteSpace(iccid))
            {
                var exact = _kernelService.IccidRoutes.FirstOrDefault(r =>
                    string.Equals(r.Iccid, iccid, StringComparison.OrdinalIgnoreCase));
                if (exact != null && !string.IsNullOrWhiteSpace(exact.PhoneNumber))
                    return exact.PhoneNumber!.Trim();
            }
            return null; // 不确定则不写
        }

        /// <summary>
        /// 服务层完成 eUICC 探测后同步左栏。只接受当前选中卡槽的结果，
        /// 避免多卡槽时把别的卡的数据套到当前卡片上。
        /// </summary>
        private void OnEuiccProbeCompleted(string slotId, EuiccProbeResult result)
        {
            void Apply()
            {
                var slot = SelectedSlot;
                if (slot == null) return;
                if (!string.Equals(slot.Id, slotId, StringComparison.OrdinalIgnoreCase)) return;
                ApplyEuiccProbeResult(result);
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(Apply));
            }
            else
            {
                Apply();
            }
        }

        private void ApplyEuiccProbeResult(EuiccProbeResult result)
        {
            // 只有拿到「可信结果」才替换列表：探测失败（切卡后 SIM REFRESH
            // 锁卡、读卡器 detach）不应把界面刷成空白。
            var hasFreshData = result.Capability == EuiccCapability.Supported || result.Profiles.Count > 0;
            if (!hasFreshData) return;

            EuiccProfiles.Clear();
            foreach (var profile in result.Profiles)
            {
                EuiccProfiles.Add(profile);
            }

            OnPropertyChanged(nameof(HasEuiccProfiles));
            OnPropertyChanged(nameof(CurrentCardDisplayName));
            OnPropertyChanged(nameof(CurrentCardPhoneNumber));
            OnPropertyChanged(nameof(CurrentCardCountryOperator));
            OnPropertyChanged(nameof(CurrentCardImsi));
            OnPropertyChanged(nameof(CurrentCardIccid));
            OnPropertyChanged(nameof(CurrentCardVoWifiState));
        }

        /// <summary>刷新当前卡片的 Profile 列表。</summary>
        [RelayCommand]
        private async Task RefreshEuiccProfilesAsync()
        {
            var slot = SelectedSlot;
            if (slot == null) return;

            try
            {
                var result = await _kernelService.ProbeEuiccAsync(slot.Id);
                ApplyEuiccProbeResult(result);
            }
            catch (Exception ex)
            {
                StatusMessage = $"读取 eSIM Profile 失败: {ex.Message}";
            }
        }

        /// <summary>切换到指定的 eSIM Profile。</summary>
        [RelayCommand]
        private async Task SwitchProfileAsync(Profile? profile)
        {
            var target = profile;
            var slot = SelectedSlot;
            if (target == null || slot == null || IsSending || IsCalling) return;

            IsCalling = true;
            StatusMessage = $"正在启用 Profile: {target.ProviderName ?? target.ICCID}...";
            try
            {
                var ok = await _kernelService.SwitchEuiccProfileAsync(target.ICCID, slot.Id, target.EuiccAid);
                StatusMessage = ok
                    ? "Profile 切换成功，新卡的号码与显示名已重新读取。"
                    : "Profile 切换失败。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"切换 Profile 失败: {ex.Message}";
            }
            finally
            {
                IsCalling = false;
            }

        }

        partial void OnMessageTextChanged(string value)
        {
            OnPropertyChanged(nameof(CharCountInfo));
            OnPropertyChanged(nameof(CharacterCountInfo));
            OnPropertyChanged(nameof(MessageInput));
            OnPropertyChanged(nameof(MessageTextInput));
            SendMessageCommand.NotifyCanExecuteChanged();
        }

        partial void OnSearchQueryChanged(string value) => ConversationsView.Refresh();

        partial void OnSelectedSlotChanged(ModemSlot? value)
        {
            OnPropertyChanged(nameof(CurrentCardDisplayName));
            OnPropertyChanged(nameof(CurrentCardPhoneNumber));
            OnPropertyChanged(nameof(CurrentCardCountryOperator));
            OnPropertyChanged(nameof(CurrentCardImsi));
            OnPropertyChanged(nameof(CurrentCardIccid));
            OnPropertyChanged(nameof(CurrentCardVoWifiState));

            // 换卡后左栏的 Profile 列表必须跟着换，否则会显示上一张卡的 Profile。
            EuiccProfiles.Clear();
            OnPropertyChanged(nameof(HasEuiccProfiles));
            if (value != null)
            {
                SafeFire.Run(RefreshEuiccProfilesAsync());
            }
        }

        partial void OnIsSendingChanged(bool value)
        {
            OnPropertyChanged(nameof(IsBusy));
            SendMessageCommand.NotifyCanExecuteChanged();
        }

        partial void OnIsCallingChanged(bool value) => OnPropertyChanged(nameof(IsBusy));

        private bool FilterConversation(object item)
        {
            if (item is not SmsConversationModel conversation || string.IsNullOrWhiteSpace(SearchQuery))
            {
                return true;
            }

            var query = SearchQuery.Trim();
            return conversation.ContactNumber.Contains(query, StringComparison.OrdinalIgnoreCase)
                || conversation.Messages.Any(message => message.Text.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        [RelayCommand]
        private async Task CallContactAsync()
        {
            if (SelectedConversation == null || IsCalling) return;
            IsCalling = true;
            StatusMessage = $"正在呼叫 {SelectedConversation.ContactNumber}...";
            try
            {
                var targetSlot = SelectedSlot ?? Slots.FirstOrDefault();
                await _kernelService.DialAsync(SelectedConversation.ContactNumber, targetSlot?.Id);
                StatusMessage = "呼叫已发起。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"呼叫失败: {ex.Message}";
            }
            finally
            {
                IsCalling = false;
            }
        }

        /// <summary>禁用当前 eSIM Profile（仅设备管理页同款能力，供卡片按钮使用）。</summary>
        [RelayCommand]
        private async Task DisableProfileAsync(Profile? profile)
        {
            var target = profile;
            if (target == null || IsSending || IsCalling) return;

            IsCalling = true;
            StatusMessage = $"正在禁用 Profile: {target.ProviderName ?? target.ICCID}...";
            try
            {
                var ok = await _kernelService.DisableEuiccProfileAsync(target.ICCID, SelectedSlot?.Id, target.EuiccAid);
                StatusMessage = ok ? "Profile 禁用成功。" : "Profile 禁用失败。";
                await RefreshEuiccProfilesAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"禁用 Profile 失败: {ex.Message}";
            }
            finally
            {
                IsCalling = false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanSendMessage))]
        private Task SendMessageAsync() => SendSmsAsync();

        private bool CanSendMessage() =>
            !IsSending && SelectedConversation != null && !string.IsNullOrWhiteSpace(MessageText);

        partial void OnSelectedConversationChanged(SmsConversationModel? value)
        {
            if (value != null)
            {
                value.UnreadCount = 0;
            }
            SendMessageCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand]
        private void StartNewChat()
        {
            IsCreatingNewChat = true;
            NewRecipientInput = string.Empty;
        }

        [RelayCommand]
        private void CancelNewChat()
        {
            IsCreatingNewChat = false;
            NewRecipientInput = string.Empty;
        }

        [RelayCommand]
        private void ConfirmNewChat()
        {
            if (string.IsNullOrWhiteSpace(NewRecipientInput)) return;
            var num = NewRecipientInput.Trim();

            // 只接受可路由的号码形态：+E.164、普通数字、或 SIP URI。
            bool isValid = (num.StartsWith("+", StringComparison.Ordinal) && num.Length >= 8 && num[1..].All(char.IsDigit))
                || (num.All(char.IsDigit) && num.Length >= 5)
                || (num.Contains('@') && !num.Contains(' '));
            if (!isValid)
            {
                StatusMessage = "号码格式无效：请输入手机号（如 +8613800138000）或 SIP 地址。";
                return;
            }

            var existing = Conversations.FirstOrDefault(c => c.ContactNumber == num);
            if (existing == null)
            {
                string[] colors = { "#0078D4", "#107C10", "#D13438", "#8764B8", "#FF8C00", "#008272" };
                int hash = Math.Abs(num.GetHashCode());
                var letter = num.Length > 0 ? (char.IsLetter(num[0]) ? num[0].ToString().ToUpper() : num[^1].ToString()) : "#";

                existing = new SmsConversationModel
                {
                    ContactNumber = num,
                    AvatarLetter = letter,
                    AvatarColor = colors[hash % colors.Length]
                };
                Conversations.Insert(0, existing);
            }

            SelectedConversation = existing;
            SelectedConversation.UnreadCount = 0;
            IsCreatingNewChat = false;
            NewRecipientInput = string.Empty;
        }

        [RelayCommand]
        private async Task DeleteConversationAsync(SmsConversationModel? conv)
        {
            var target = conv ?? SelectedConversation;
            if (target != null)
            {
                var uiMessageBox = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "确认删除会话",
                    Content = $"确定要删除与 {target.ContactNumber} 的全部会话记录吗？\n此操作不可恢复。",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消"
                };

                var result = await uiMessageBox.ShowDialogAsync();
                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    _kernelService.DeleteConversation(target.ContactNumber);
                    if (SelectedConversation == target)
                    {
                        SelectedConversation = Conversations.FirstOrDefault();
                    }
                    StatusMessage = $"已删除与 {target.ContactNumber} 的会话。";
                }
            }
        }

        [RelayCommand]
        private async Task ClearConversationAsync()
        {
            if (SelectedConversation != null)
            {
                var uiMessageBox = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "确认清空会话",
                    Content = $"确定要清空与 {SelectedConversation.ContactNumber} 的所有短信记录吗？\n此操作不可恢复。",
                    PrimaryButtonText = "清空",
                    CloseButtonText = "取消"
                };

                var result = await uiMessageBox.ShowDialogAsync();
                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    _kernelService.ClearConversation(SelectedConversation.ContactNumber);
                    StatusMessage = $"已清空与 {SelectedConversation.ContactNumber} 的短信记录。";
                }
            }
        }

        [RelayCommand]
        private void CopyOtpCode(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            try
            {
                System.Windows.Clipboard.SetText(code.Trim());
                StatusMessage = $"验证码 {code.Trim()} 已复制到剪贴板。";
                AppToast.ShowCopySuccess($"短信验证码 {code.Trim()}");
            }
            catch (Exception ex)
            {
                StatusMessage = $"复制失败: {ex.Message}";
            }
        }

        [RelayCommand]
        private void CopyContactNumber()
        {
            if (SelectedConversation == null || string.IsNullOrWhiteSpace(SelectedConversation.ContactNumber)) return;
            try
            {
                System.Windows.Clipboard.SetText(SelectedConversation.ContactNumber.Trim());
                StatusMessage = $"号码 {SelectedConversation.ContactNumber.Trim()} 已复制到剪贴板。";
                AppToast.ShowCopySuccess($"联系人号码 {SelectedConversation.ContactNumber.Trim()}");
            }
            catch (Exception ex)
            {
                StatusMessage = $"复制失败: {ex.Message}";
            }
        }

        [RelayCommand]
        private async Task RetryMessageAsync(SmsMessageModel? msg)
        {
            if (msg == null || SelectedConversation == null) return;
            // 只重试当前会话内的消息，避免跨会话重发发错人。
            if (!SelectedConversation.Messages.Contains(msg)) return;
            MessageText = msg.Text;
            await SendSmsAsync();
        }

        [RelayCommand]
        private void TestOtpNotification()
        {
            var testSms = new SmsMessageModel
            {
                SenderOrRecipient = "10690007890",
                Text = "【阿里云】您正在登录控制台，验证码为 839201，请在 5 分钟内完成验证。切勿告知他人！",
                Timestamp = DateTime.UtcNow,
                IsOutgoing = false,
                DeliveryState = SmsDeliveryState.Received
            };
            SoundEffectService.Instance.PlaySmsChime();
            Views.Windows.IncomingSmsFloatingWindow.ShowNotification(testSms, SelectedSlot?.Name ?? "SIM 1");
            StatusMessage = "已触发验证码短信悬浮窗测试。";
        }

        [RelayCommand]
        private void CopyMessage(object? parameter)
        {
            string? text = null;
            if (parameter is SmsMessageModel model)
            {
                text = model.Text;
            }
            else if (parameter is string s)
            {
                text = s;
            }
            else if (parameter == null && SelectedConversation?.Messages.LastOrDefault() is SmsMessageModel last)
            {
                text = last.Text;
            }

            if (string.IsNullOrWhiteSpace(text)) return;

            try
            {
                System.Windows.Clipboard.SetDataObject(text, true);
                StatusMessage = "短信内容已成功复制到剪贴板。";
                AppToast.ShowCopySuccess("短信内容");
            }
            catch
            {
                try
                {
                    System.Windows.Clipboard.SetText(text);
                    StatusMessage = "短信内容已成功复制到剪贴板。";
                    AppToast.ShowCopySuccess("短信内容");
                }
                catch (Exception ex)
                {
                    StatusMessage = $"复制失败: {ex.Message}";
                }
            }
        }

        [RelayCommand]
        private void DeleteMessage(SmsMessageModel? msg)
        {
            if (SelectedConversation != null && msg != null)
            {
                _kernelService.DeleteMessage(SelectedConversation.ContactNumber, msg.Id);
            }
        }

        private async Task SendSmsAsync()
        {
            if (SelectedConversation == null || string.IsNullOrWhiteSpace(MessageText)) return;
            var text = MessageText.Trim();
            var recipient = SelectedConversation.ContactNumber;

            IsSending = true;
            StatusMessage = "正在发送短信...";

            try
            {
                MessageText = string.Empty;
                var targetSlot = SelectedSlot ?? Slots.FirstOrDefault();
                var res = await _kernelService.SendSmsAsync(
                    recipient,
                    text,
                    RequestDeliveryReport,
                    targetSlot?.Id,
                    forceVowifi: !ForceCellular,
                    forceCellular: ForceCellular);

                StatusMessage = res.AllPartsAccepted ? "短信已成功提交发送。" : $"部分发送: {res.SubmissionStatus}";
            }
            catch (Exception ex)
            {
                if (string.IsNullOrWhiteSpace(MessageText))
                {
                    MessageText = text;
                }
                StatusMessage = $"发送失败: {ex.Message}";
            }
            finally
            {
                IsSending = false;
            }
        }
    }
}
