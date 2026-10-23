import { describe, expect, it } from 'vitest';
import { ApiError } from '../services/apiClient';
import { describeAuthError } from './authErrors';

const validation = (errors: Record<string, string[]>) =>
  new ApiError('One or more validation errors occurred.', 400, 'VALIDATION_FAILED', { status: 400, errors });

describe('describeAuthError', () => {
  it('maps Identity duplicate and invalid email codes to the email field', () => {
    expect(describeAuthError(validation({ DuplicateEmail: ['x'], DuplicateUserName: ['x'] }), 'register')).toEqual({
      email: 'An account with this email already exists. Sign in instead.',
    });
    expect(describeAuthError(validation({ InvalidEmail: ["Email 'bad' is invalid."] }), 'register').email).toBe(
      'Enter a valid email address.',
    );
    expect(describeAuthError(validation({ InvalidUserName: ['x'] }), 'register').email).toBe('Enter a valid email address.');
  });

  it('collects every password rule the server rejected', () => {
    const result = describeAuthError(
      validation({
        PasswordTooShort: ['Passwords must be at least 8 characters.'],
        PasswordRequiresDigit: ["Passwords must have at least one digit ('0'-'9')."],
      }),
      'register',
    );
    expect(result.password).toBe("Passwords must be at least 8 characters. Passwords must have at least one digit ('0'-'9').");
    expect(result.email).toBeUndefined();
  });

  it('shows an unknown validation code above the form, and never returns nothing', () => {
    expect(describeAuthError(validation({ Something: ['Server says no.'] }), 'register')).toEqual({ form: 'Server says no.' });
    expect(describeAuthError(validation({ Something: [] }), 'register')).toEqual({ form: 'Check your details and try again.' });
  });

  it('distinguishes a wrong password from a locked account', () => {
    const failed = new ApiError('Unauthorized', 401, 'UNAUTHORIZED', { status: 401, detail: 'Failed' });
    const locked = new ApiError('Unauthorized', 401, 'UNAUTHORIZED', { status: 401, detail: 'LockedOut' });

    expect(describeAuthError(failed, 'sign-in').form).toMatch(/don’t match an account/);
    expect(describeAuthError(locked, 'sign-in').form).toMatch(/locked/);
  });

  it.each([
    ['a server error', new ApiError('The store is having a problem right now.', 500, 'HTTP_500')],
    ['an unavailable database (503 is not a guarantee for accounts)', new ApiError("The store's database is unavailable.", 503, 'DATABASE_UNAVAILABLE')],
    ['a lost response', new ApiError("We couldn't reach the store.", 0, 'NETWORK_ERROR')],
  ])('after %s during registration, says the account may exist and to try signing in first', (_case, outage) => {
    const form = describeAuthError(outage, 'register').form;

    expect(form).toContain(outage.message);
    expect(form).toMatch(/couldn't confirm whether your account was created\. Try signing in first/);
    expect(describeAuthError(outage, 'sign-in').form).toBe(outage.message);
  });

  it('passes through rate-limit and network messages and handles non-API errors', () => {
    const limited = new ApiError('Too many requests. Wait 30 seconds and try again.', 429, 'TOO_MANY_REQUESTS');

    expect(describeAuthError(limited, 'sign-in').form).toBe('Too many requests. Wait 30 seconds and try again.');
    expect(describeAuthError(new Error('boom'), 'sign-in').form).toBe('Something went wrong. Try again.');
  });
});
