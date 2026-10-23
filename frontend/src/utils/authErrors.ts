import { ApiError } from '../services/apiClient';

export type AuthMode = 'sign-in' | 'register';

export interface AuthErrors {
  email?: string;
  password?: string;
  form?: string;
}

/** Maps the API's error to the field it belongs to, in words a shopper can act on. */
export function describeAuthError(error: unknown, mode: AuthMode): AuthErrors {
  if (!(error instanceof ApiError)) return { form: 'Something went wrong. Try again.' };

  if (error.status === 401) {
    return error.problem?.detail === 'LockedOut'
      ? { form: 'Too many failed attempts. This account is locked for a few minutes; try again later.' }
      : { form: 'That email and password don’t match an account. Check them and try again.' };
  }

  if (error.code === 'VALIDATION_FAILED' && error.problem?.errors) {
    const result: AuthErrors = {};
    const passwordMessages: string[] = [];
    for (const [key, messages] of Object.entries(error.problem.errors)) {
      const message = messages[0];
      if (!message) continue;
      if (key === 'DuplicateEmail' || key === 'DuplicateUserName') {
        result.email = 'An account with this email already exists. Sign in instead.';
      } else if (key.startsWith('InvalidEmail') || key.startsWith('InvalidUserName') || key.toLowerCase() === 'email') {
        result.email ??= 'Enter a valid email address.';
      } else if (key.startsWith('Password') || key.toLowerCase() === 'password') {
        passwordMessages.push(message);
      } else {
        result.form ??= message;
      }
    }
    if (passwordMessages.length) result.password = passwordMessages.join(' ');
    if (!result.email && !result.password && !result.form) result.form = 'Check your details and try again.';
    return result;
  }

  // Rate limiting, network failures and outages: the client's message already says what to do.
  return { form: mode === 'register' && error.status >= 500 ? `${error.message} Your account may not have been created.` : error.message };
}
