import type { CustomerDetails } from '../types/checkout';

// Same rules as the server's CheckoutRequestValidator. These exist for fast feedback;
// the server re-validates everything.
export const NAME_MIN_LENGTH = 2;
export const NAME_MAX_LENGTH = 100;
export const EMAIL_MAX_LENGTH = 254;
const EMAIL_PATTERN = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

export const isValidEmail = (email: string) => email.length <= EMAIL_MAX_LENGTH && EMAIL_PATTERN.test(email);

export type CustomerErrors = Partial<Record<keyof CustomerDetails, string>>;

export function validateCustomer(customer: CustomerDetails): CustomerErrors {
  const errors: CustomerErrors = {};
  const name = customer.name.trim();
  const email = customer.email.trim();

  if (name.length === 0) {
    errors.name = 'Enter your name.';
  } else if (name.length < NAME_MIN_LENGTH || name.length > NAME_MAX_LENGTH) {
    errors.name = `Name must be between ${NAME_MIN_LENGTH} and ${NAME_MAX_LENGTH} characters.`;
  }

  if (email.length === 0) {
    errors.email = 'Enter your email address.';
  } else if (!isValidEmail(email)) {
    errors.email = 'Enter an email address like name@example.com.';
  }

  return errors;
}
