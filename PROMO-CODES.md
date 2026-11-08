# Website promo codes

Owners and managers can add, edit, enable or disable promo codes in **Website settings → Promo codes** in the POS. Set an optional minimum food subtotal for each code; delivery does not count towards it. Save changes to apply them to new orders. The optional dates are entered in the staff device's local time; the end is exclusive. Codes apply to the food subtotal, excluding delivery. Codes are reusable unless “Once per phone number” is selected. Phone formats are normalized, including UK 07 and +44 forms. Card redemptions are recorded after payment; overlapping checkouts can both receive the discount. Card payments retain the discount accepted when the checkout session was created. A 100% discount can only be used with cash unless delivery brings the card total above Stripe's minimum.

Before the first save in POS, codes configured in `WebsiteCheckout:PromoCodes` are shown and used. Saving replaces that configuration with the POS-managed list.
