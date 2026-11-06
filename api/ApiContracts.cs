record CompanyInput(string Name, string Cnpj);
record BootstrapInput(string Name, string Username, string Pin);
record LoginInput(string Username, string Pin);
record UserAccessInput(string Name, string Username, string Pin, string Role = "admin", bool CanManagePlanRates = false);
record LinkUserInput(string Role = "admin", bool CanManagePlanRates = false);
record AccessUpdateInput(string Role = "admin", bool Active = true, bool CanManagePlanRates = false);
record PartyInput(string Name, string Document, string Type, string Phone = "", string Email = "", string PersonType = "");
record AccountInput(string Name, long Opening, string Type = "cash");
record PlanInput(
    string Name, string Kind = "payable",
    decimal InterestRate = 0, string InterestMode = "percentage", long InterestValue = 0,
    decimal PenaltyRate = 0, string PenaltyMode = "percentage", long PenaltyValue = 0,
    decimal DiscountRate = 0, string DiscountMode = "percentage", long DiscountValue = 0);
record NamedInput(string Name);
record NamedKindInput(string Name, string Kind = "both");
record CreditCardInput(string Name, int ClosingDay, int DueDay, int? AccountId = null, string Notes = "", string LastFour = "");
record CreditCardPurchaseInput(string Description, long Amount, string PurchaseDate, int Installments = 1, string Category = "CARTÃO DE CRÉDITO", string Notes = "", string Mode = "single");
record CreditCardPurchaseDeleteInput(string Reason);
record TitleInput(string Kind, string Description, long Amount, string Due, int? PartyId, string Category, string Notes, int Installments = 1, decimal InterestRate = 0, decimal PenaltyRate = 0, decimal DiscountRate = 0, long[]? InstallmentAmounts = null, string[]? InstallmentDues = null, string DocumentNumber = "", string IssueDate = "", string Branch = "", string CostCenter = "", int InstallmentIntervalDays = 0, int? PaymentMethodId = null);
record SettlementInput(int AccountId, long Principal, long Interest, long Penalty, long Discount, string Date, long Additional = 0, int? PaymentMethodId = null);
record BulkSettlementItem(int TitleId, long Principal, long Interest = 0, long Penalty = 0, long Discount = 0, long Additional = 0);
record BulkSettlementInput(int AccountId, string Date, BulkSettlementItem[] Items, int? PaymentMethodId = null);
record OfxPreviewInput(int AccountId, string FileName, string DataBase64);
record OfxConfirmItem(long TransactionId, int TitleId = 0, bool Create = false, string Description = "", string Category = "", long? ExistingSettlementId = null);
record OfxConfirmInput(long ImportId, OfxConfirmItem[] Items, bool UpdateBalance = false);
record ParsedOfxTransaction(string FitId, string Date, long Amount, string Memo, string Type);
record ParsedOfx(string BankId, string Start, string End, List<ParsedOfxTransaction> Transactions, long? Balance, string BalanceDate);
record ReasonInput(string Reason);
record CancelInput(string Reason, string Scope = "single");
record EditTitleInput(string Description, long Amount, string Due, int? PartyId, string Category, string Notes, string Scope = "single", string DocumentNumber = "", string IssueDate = "", string Branch = "", string CostCenter = "", int? PaymentMethodId = null);
record DeleteInput(string Reason, string Scope = "single");
record TransferInput(int FromId, int ToId, long Amount, string Date);
record AdjustmentInput(long Amount, string Date, string Reason);
record DuplicateInput(string Due = "");
record AttachmentInput(int? TitleId, int? SettlementId, string Name, string MimeType, string DataBase64);
record RestoreInput(string Name, string DataBase64);
record AttachmentFile(byte[] Content, string MimeType, string Name);
record DownloadFile(byte[] Content, string FileName);
