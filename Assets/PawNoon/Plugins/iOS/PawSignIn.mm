// Paw Noon M6: native Sign in with Apple and Google for iOS, no third-party SDK.
//
// Unity -> native (C# NativeSignIn.cs, [DllImport("__Internal")]):
//   int  PawSignIn_AppleAvailable(void)                         1 on iOS 13+
//   void PawSignIn_Apple(const char* nonceSha256Hex)            ASAuthorizationAppleIDProvider sheet
//   void PawSignIn_Google(const char* iosClientId, const char* nonceSha256Hex)
//                                                               ASWebAuthenticationSession + PKCE, code -> id_token
// native -> Unity (main thread):
//   UnitySendMessage("PawSignIn", "OnNativeResult", json)
//   json = {"ok":true,"provider":"apple","idToken":"<jwt>","authCode":"<apple code>"}
//        | {"ok":false,"provider":"google","cancelled":true,"error":"cancelled"}
//
// The nonce handed in is already the SHA-256 hex of the raw nonce that C# later sends to Supabase; Apple and Google
// copy it verbatim into the ID token's "nonce" claim, Supabase hashes the raw one and compares.
// Compiled with -fobjc-arc (see PawSignIn.mm.meta); AuthenticationServices + Security frameworks are linked from
// the plugin settings, the Sign in with Apple capability by Code/Editor/PawSignInPostBuild.cs.

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <AuthenticationServices/AuthenticationServices.h>
#import <CommonCrypto/CommonDigest.h>
#import <Security/Security.h>
#include "UnityInterface.h"

#if !__has_feature(objc_arc)
#error "PawSignIn.mm must be compiled with ARC (-fobjc-arc): check Assets/PawNoon/Plugins/iOS/PawSignIn.mm.meta"
#endif

static void PawSend(NSDictionary* payload)
{
    NSData* data = [NSJSONSerialization dataWithJSONObject:payload options:0 error:nil];
    NSString* json = data ? [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding]
                          : @"{\"ok\":false,\"error\":\"native_json\"}";
    dispatch_async(dispatch_get_main_queue(), ^{
        UnitySendMessage("PawSignIn", "OnNativeResult", [json UTF8String]);
    });
}

static void PawFail(NSString* provider, NSString* error, BOOL cancelled)
{
    PawSend(@{ @"ok": @NO, @"provider": provider, @"cancelled": @(cancelled), @"error": error ?: @"error" });
}

static NSString* PawString(const char* s)
{
    return s ? [NSString stringWithUTF8String:s] : @"";
}

static UIWindow* PawWindow(void)
{
    UIWindow* w = UnityGetMainWindow();
    if (w == nil) w = UnityGetGLView().window;
    return w;
}

static NSString* PawBase64Url(NSData* data)
{
    NSString* s = [data base64EncodedStringWithOptions:0];
    s = [s stringByReplacingOccurrencesOfString:@"+" withString:@"-"];
    s = [s stringByReplacingOccurrencesOfString:@"/" withString:@"_"];
    return [s stringByReplacingOccurrencesOfString:@"=" withString:@""];
}

static NSString* PawRandomUrlSafe(size_t bytes)
{
    NSMutableData* d = [NSMutableData dataWithLength:bytes];
    if (SecRandomCopyBytes(kSecRandomDefault, bytes, d.mutableBytes) != errSecSuccess) return nil;
    return PawBase64Url(d);
}

static NSString* PawPkceChallenge(NSString* verifier)
{
    NSData* in = [verifier dataUsingEncoding:NSASCIIStringEncoding];
    unsigned char digest[CC_SHA256_DIGEST_LENGTH];
    CC_SHA256(in.bytes, (CC_LONG)in.length, digest);
    return PawBase64Url([NSData dataWithBytes:digest length:CC_SHA256_DIGEST_LENGTH]);
}

static NSString* PawFormEncode(NSString* s)
{
    NSMutableCharacterSet* allowed = [[NSCharacterSet alphanumericCharacterSet] mutableCopy];
    [allowed addCharactersInString:@"-._~"];
    return [s stringByAddingPercentEncodingWithAllowedCharacters:allowed];
}

// ------------------------------------------------------------------------------------------------ Apple

API_AVAILABLE(ios(13.0))
@interface PawAppleSignIn : NSObject <ASAuthorizationControllerDelegate, ASAuthorizationControllerPresentationContextProviding>
@property (nonatomic, strong) ASAuthorizationController* controller;
@end

static PawAppleSignIn* gPawApple = nil;   // strong: alive until the sheet answers

@implementation PawAppleSignIn

- (void)startWithNonce:(NSString*)nonceHash
{
    ASAuthorizationAppleIDProvider* provider = [[ASAuthorizationAppleIDProvider alloc] init];
    ASAuthorizationAppleIDRequest* request = [provider createRequest];
    request.requestedScopes = @[];   // no name, no email: the game only needs a stable account id
    if (nonceHash.length > 0) request.nonce = nonceHash;
    self.controller = [[ASAuthorizationController alloc] initWithAuthorizationRequests:@[ request ]];
    self.controller.delegate = self;
    self.controller.presentationContextProvider = self;
    [self.controller performRequests];
}

- (void)finish
{
    self.controller = nil;
    if (gPawApple == self) gPawApple = nil;
}

- (void)authorizationController:(ASAuthorizationController*)controller didCompleteWithAuthorization:(ASAuthorization*)authorization
{
    id credential = authorization.credential;
    if ([credential isKindOfClass:[ASAuthorizationAppleIDCredential class]])
    {
        ASAuthorizationAppleIDCredential* c = (ASAuthorizationAppleIDCredential*)credential;
        NSString* token = c.identityToken ? [[NSString alloc] initWithData:c.identityToken encoding:NSUTF8StringEncoding] : nil;
        NSString* code = c.authorizationCode ? [[NSString alloc] initWithData:c.authorizationCode encoding:NSUTF8StringEncoding] : @"";
        if (token.length > 0) PawSend(@{ @"ok": @YES, @"provider": @"apple", @"idToken": token, @"authCode": code ?: @"" });
        else PawFail(@"apple", @"no_token", NO);
    }
    else
    {
        PawFail(@"apple", @"unexpected_credential", NO);
    }
    [self finish];
}

- (void)authorizationController:(ASAuthorizationController*)controller didCompleteWithError:(NSError*)error
{
    BOOL cancelled = error.code == ASAuthorizationErrorCanceled;
    PawFail(@"apple", cancelled ? @"cancelled" : [NSString stringWithFormat:@"apple_%ld", (long)error.code], cancelled);
    [self finish];
}

- (ASPresentationAnchor)presentationAnchorForAuthorizationController:(ASAuthorizationController*)controller
{
    return PawWindow();
}

@end

// ------------------------------------------------------------------------------------------------ Google

API_AVAILABLE(ios(13.0))
@interface PawGoogleSignIn : NSObject <ASWebAuthenticationPresentationContextProviding>
@property (nonatomic, strong) ASWebAuthenticationSession* session;
@property (nonatomic, copy) NSString* clientId;
@property (nonatomic, copy) NSString* redirectUri;
@property (nonatomic, copy) NSString* verifier;
@property (nonatomic, copy) NSString* state;
@end

static PawGoogleSignIn* gPawGoogle = nil;

@implementation PawGoogleSignIn

// "123-abc.apps.googleusercontent.com" -> "com.googleusercontent.apps.123-abc" (Google's iOS redirect scheme)
+ (NSString*)reversedClientId:(NSString*)clientId
{
    NSString* suffix = @".apps.googleusercontent.com";
    if (![clientId hasSuffix:suffix]) return nil;
    return [@"com.googleusercontent.apps." stringByAppendingString:[clientId substringToIndex:clientId.length - suffix.length]];
}

- (void)startWithClientId:(NSString*)clientId nonce:(NSString*)nonceHash
{
    NSString* scheme = [PawGoogleSignIn reversedClientId:clientId];
    self.verifier = PawRandomUrlSafe(32);
    self.state = PawRandomUrlSafe(16);
    if (scheme == nil || self.verifier == nil || self.state == nil) { PawFail(@"google", @"unavailable", NO); [self finish]; return; }
    self.clientId = clientId;
    self.redirectUri = [scheme stringByAppendingString:@":/oauthredirect"];

    NSURLComponents* u = [NSURLComponents componentsWithString:@"https://accounts.google.com/o/oauth2/v2/auth"];
    NSMutableArray<NSURLQueryItem*>* q = [NSMutableArray array];
    [q addObject:[NSURLQueryItem queryItemWithName:@"client_id" value:clientId]];
    [q addObject:[NSURLQueryItem queryItemWithName:@"redirect_uri" value:self.redirectUri]];
    [q addObject:[NSURLQueryItem queryItemWithName:@"response_type" value:@"code"]];
    [q addObject:[NSURLQueryItem queryItemWithName:@"scope" value:@"openid email"]];
    [q addObject:[NSURLQueryItem queryItemWithName:@"code_challenge" value:PawPkceChallenge(self.verifier)]];
    [q addObject:[NSURLQueryItem queryItemWithName:@"code_challenge_method" value:@"S256"]];
    [q addObject:[NSURLQueryItem queryItemWithName:@"state" value:self.state]];
    [q addObject:[NSURLQueryItem queryItemWithName:@"prompt" value:@"select_account"]];
    if (nonceHash.length > 0) [q addObject:[NSURLQueryItem queryItemWithName:@"nonce" value:nonceHash]];
    u.queryItems = q;

    __weak PawGoogleSignIn* weakSelf = self;
    self.session = [[ASWebAuthenticationSession alloc] initWithURL:u.URL callbackURLScheme:scheme
        completionHandler:^(NSURL* callbackURL, NSError* error) {
            PawGoogleSignIn* me = weakSelf;
            if (me == nil) return;
            [me handleCallback:callbackURL error:error];
        }];
    self.session.presentationContextProvider = self;
    self.session.prefersEphemeralWebBrowserSession = NO;   // keeps the player's Google cookie: one tap next time
    if (![self.session start]) { PawFail(@"google", @"session_failed", NO); [self finish]; }
}

- (void)finish
{
    self.session = nil;
    if (gPawGoogle == self) gPawGoogle = nil;
}

- (void)handleCallback:(NSURL*)url error:(NSError*)error
{
    if (error != nil)
    {
        BOOL cancelled = [error.domain isEqualToString:ASWebAuthenticationSessionErrorDomain] &&
                         error.code == ASWebAuthenticationSessionErrorCodeCanceledLogin;
        PawFail(@"google", cancelled ? @"cancelled" : [NSString stringWithFormat:@"google_%ld", (long)error.code], cancelled);
        [self finish];
        return;
    }
    NSString* code = nil;
    NSString* state = nil;
    NSString* oauthError = nil;
    for (NSURLQueryItem* item in [NSURLComponents componentsWithURL:url resolvingAgainstBaseURL:NO].queryItems)
    {
        if ([item.name isEqualToString:@"code"]) code = item.value;
        else if ([item.name isEqualToString:@"state"]) state = item.value;
        else if ([item.name isEqualToString:@"error"]) oauthError = item.value;
    }
    if (oauthError != nil) { PawFail(@"google", [@"google_" stringByAppendingString:oauthError], [oauthError isEqualToString:@"access_denied"]); [self finish]; return; }
    if (code.length == 0 || ![state isEqualToString:self.state]) { PawFail(@"google", @"bad_callback", NO); [self finish]; return; }

    // authorization code -> tokens (installed-app client: PKCE verifier, no secret)
    NSMutableURLRequest* req = [NSMutableURLRequest requestWithURL:[NSURL URLWithString:@"https://oauth2.googleapis.com/token"]];
    req.HTTPMethod = @"POST";
    req.timeoutInterval = 15;
    [req setValue:@"application/x-www-form-urlencoded" forHTTPHeaderField:@"Content-Type"];
    NSString* form = [NSString stringWithFormat:@"code=%@&client_id=%@&redirect_uri=%@&grant_type=authorization_code&code_verifier=%@",
                      PawFormEncode(code), PawFormEncode(self.clientId), PawFormEncode(self.redirectUri), PawFormEncode(self.verifier)];
    req.HTTPBody = [form dataUsingEncoding:NSUTF8StringEncoding];
    PawGoogleSignIn* me = self;
    [[[NSURLSession sharedSession] dataTaskWithRequest:req completionHandler:^(NSData* data, NSURLResponse* response, NSError* netError) {
        if (netError != nil || data == nil) { PawFail(@"google", @"offline", NO); dispatch_async(dispatch_get_main_queue(), ^{ [me finish]; }); return; }
        NSDictionary* json = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
        NSString* idToken = [json isKindOfClass:[NSDictionary class]] ? json[@"id_token"] : nil;
        if ([idToken isKindOfClass:[NSString class]] && idToken.length > 0) PawSend(@{ @"ok": @YES, @"provider": @"google", @"idToken": idToken });
        else PawFail(@"google", @"token_exchange_failed", NO);
        dispatch_async(dispatch_get_main_queue(), ^{ [me finish]; });
    }] resume];
}

- (ASPresentationAnchor)presentationAnchorForWebAuthenticationSession:(ASWebAuthenticationSession*)session
{
    return PawWindow();
}

@end

// ------------------------------------------------------------------------------------------------ C entry points

extern "C" {

int PawSignIn_AppleAvailable(void)
{
    if (@available(iOS 13.0, *)) return 1;
    return 0;
}

void PawSignIn_Apple(const char* nonceSha256Hex)
{
    NSString* nonce = PawString(nonceSha256Hex);
    dispatch_async(dispatch_get_main_queue(), ^{
        if (@available(iOS 13.0, *))
        {
            if (gPawApple != nil) { PawFail(@"apple", @"busy", NO); return; }
            PawAppleSignIn* a = [[PawAppleSignIn alloc] init];
            gPawApple = a;
            [a startWithNonce:nonce];
        }
        else
        {
            PawFail(@"apple", @"unavailable", NO);
        }
    });
}

void PawSignIn_Google(const char* iosClientId, const char* nonceSha256Hex)
{
    NSString* clientId = PawString(iosClientId);
    NSString* nonce = PawString(nonceSha256Hex);
    dispatch_async(dispatch_get_main_queue(), ^{
        if (@available(iOS 13.0, *))
        {
            if (gPawGoogle != nil) { PawFail(@"google", @"busy", NO); return; }
            PawGoogleSignIn* g = [[PawGoogleSignIn alloc] init];
            gPawGoogle = g;
            [g startWithClientId:clientId nonce:nonce];
        }
        else
        {
            PawFail(@"google", @"unavailable", NO);
        }
    });
}

}
