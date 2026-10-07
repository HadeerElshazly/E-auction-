/**
 * What a bidder is told when the bid path refuses their bid.
 *
 * Its own module rather than a constant inside BidBox, so that it can be tested
 * without the component: importing the component pulls in the shared config, which
 * reads `import.meta.env` and exists only inside a Vite build. A lookup table that
 * cannot be checked is how AuctionClosed went unmapped.
 *
 * The keys are RejectionReason from src/EAuction.Core/BidOutcome.cs, plus
 * BidderMismatch, which the catcher returns as a bare string rather than from the
 * enum. reasons.test.ts asserts the set is complete.
 */
export const reasons: Record<string, string> = {
  BelowOpeningPrice: 'المبلغ أقل من سعر الافتتاح.',
  BelowMinimumIncrement: 'ارتفع السعر قبل وصول مزايدتك — زايد مرة أخرى على السعر الجديد.',
  OutsideWindow: 'المزاد غير مفتوح للمزايدة الآن.',
  // Reachable by anyone who presses the button as the gavel falls, which is the
  // worst possible moment to be shown an English identifier.
  AuctionClosed: 'أُغلق المزاد قبل وصول مزايدتك.',
  NotEligible: 'اشتراكك غير مؤهّل للمزايدة في هذا المزاد.',
  RateLimited: 'مزايدات كثيرة في وقت قصير — أعد المحاولة بعد لحظة.',
  BadSignature: 'تعذّر التحقق من توقيع المزايدة. أعد تحميل الصفحة.',
  BidderMismatch: 'المزايدة مُسجَّلة باسم مزايد آخر.',
  UnknownAuction: 'المزاد غير معروف لخدمة المزايدة بعد.',
  SelfOutbid: 'أنت الأعلى بالفعل.',
  DuplicateBidId: 'أُرسلت هذه المزايدة مسبقاً.',
  MalformedFrame: 'المزايدة غير مكتملة. أعد تحميل الصفحة.',
}
