namespace EAuction.Participant.Domain;

/// <summary>
/// The citizen lane from slide 6: buy the booklet, accept the terms, pay the
/// deposit, become eligible to bid.
/// </summary>
public enum SubscriptionStatus
{
    Draft = 0,

    /// <summary>شراء كراسة الشروط</summary>
    BookletPurchased = 1,

    /// <summary>الموافقة على الشروط والأحكام</summary>
    TermsAccepted = 2,

    /// <summary>Deposit chosen, not yet settled — payment pending or guarantee unverified.</summary>
    AwaitingDeposit = 3,

    /// <summary>May bid. This is the only status the catcher ever sees as true.</summary>
    Eligible = 4,

    Revoked = 5
}

public enum DepositMethod
{
    /// <summary>دفع التأمين إلكترونيا</summary>
    Payment = 0,

    /// <summary>رفع الضمان البنكي</summary>
    BankGuarantee = 1
}
